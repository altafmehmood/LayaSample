using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents.Images;
using LayaSample.Api.Services.Documents.Pdf;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace LayaSample.Api.Services.Documents;

public interface IDocumentClassifier
{
    /// <summary>Classifies the document from its content (not its name or client-supplied content type).</summary>
    /// <exception cref="DocumentException">Unsupported (415) or unreadable/encrypted (422) document.</exception>
    DocumentClassification Classify(Stream stream);
}

public static class DocumentRoutes
{
    public const string Form = "form-agent";
    public const string Vision = "vision-agent";
    public const string Text = "text-agent";
    public const string Spreadsheet = "spreadsheet-agent";

    public static readonly string[] All = [Form, Vision, Text, Spreadsheet];
}

public sealed class DocumentClassifier(IOptions<DocumentAnalysisOptions> options) : IDocumentClassifier
{
    private const string Unsupported = "unsupported document type; expected PDF, .xlsx, .docx, TIFF, PNG or JPEG";

    private static readonly IPdfDetector[] PdfDetectors =
    [
        new AcroFormDetector(),
        new XfaDetector(),
        new EmbeddedFileDetector(),
        new TextLayerDetector(),
        new ImageDetector(),
        new LayoutDetector(),
        new AnnotationDetector()
    ];

    private readonly DocumentAnalysisOptions _options = options.Value;

    public DocumentClassification Classify(Stream stream)
    {
        stream.Position = 0;
        var mediaType = DocumentSniffer.Sniff(stream);
        return mediaType switch
        {
            DocumentSniffer.Pdf => ClassifyPdf(StreamBytes.Read(stream)),
            DocumentSniffer.Xlsx => Office(DocumentKind.Spreadsheet, mediaType, DocumentRoutes.Spreadsheet, "Excel workbook; convert sheets to markdown tables"),
            DocumentSniffer.Docx => Office(DocumentKind.WordDocument, mediaType, DocumentRoutes.Text, "Word document; convert to markdown"),
            _ when DocumentSniffer.IsImage(mediaType) => ClassifyImage(StreamBytes.Read(stream), mediaType),
            DocumentSniffer.Ole => throw new DocumentException(
                "legacy (.xls/.doc) or password-protected Office files are not supported; save as unprotected .xlsx/.docx",
                StatusCodes.Status415UnsupportedMediaType),
            _ => throw new DocumentException(Unsupported, StatusCodes.Status415UnsupportedMediaType)
        };
    }

    private DocumentClassification ClassifyPdf(byte[] bytes)
    {
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            var inspection = new PdfInspection(pdf, bytes, _options);

            foreach (var detector in PdfDetectors) detector.InspectDocument(inspection);
            // One pass over the pages so each is parsed once, however many detectors look at it.
            foreach (var page in pdf.GetPages())
            {
                var signals = new PageSignals(page.Number);
                foreach (var detector in PdfDetectors) detector.InspectPage(inspection, page, signals);
                inspection.Pages.Add(signals);
            }

            return PdfClassificationRules.Decide(inspection.Document, inspection.Pages, _options);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new DocumentException("PDF is password protected", StatusCodes.Status422UnprocessableEntity, ex);
        }
        // PdfPig surfaces malformed input as a wide range of exception types.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DocumentException("PDF could not be read", StatusCodes.Status422UnprocessableEntity, ex);
        }
    }

    private DocumentClassification ClassifyImage(byte[] bytes, string mediaType)
    {
        IReadOnlyList<RasterPage> pages;
        try
        {
            pages = RasterImages.Inspect(bytes, mediaType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DocumentException("image could not be read", StatusCodes.Status422UnprocessableEntity, ex);
        }

        // An image has no text layer: OCR is the only source of text.
        var strategy = _options.EnableOcr ? PreparationStrategy.Hybrid : PreparationStrategy.RenderToPng;
        var classified = pages
            .Select((p, i) => new PageClassification(i + 1, p.IsFax ? PageContent.Fax : PageContent.Scanned, strategy, 0, 1, 0, null))
            .ToList();
        var fax = classified.Count(p => p.Content == PageContent.Fax);

        return new DocumentClassification
        {
            Kind = DocumentKind.Image,
            Strategy = strategy,
            Route = DocumentRoutes.Vision,
            Reason = $"{mediaType} image, {pages.Count} page(s)" + (fax > 0 ? $", {fax} fax-encoded" : "")
                     + (_options.EnableOcr ? "; page images plus OCR text" : "; converted to PNG"),
            MediaType = mediaType,
            PageCount = pages.Count,
            ImageCount = pages.Count,
            Pages = classified
        };
    }

    private static DocumentClassification Office(DocumentKind kind, string mediaType, string route, string reason) => new()
    {
        Kind = kind,
        Strategy = PreparationStrategy.Markdown,
        Route = route,
        Reason = reason,
        MediaType = mediaType
    };
}

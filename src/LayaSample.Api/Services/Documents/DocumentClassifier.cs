using System.IO.Compression;
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
    /// <exception cref="DocumentException">Unsupported (415) or unreadable/encrypted/oversized (422) document.</exception>
    DocumentClassification Classify(Stream stream, CancellationToken ct = default);
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

    public DocumentClassification Classify(Stream stream, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        stream.Position = 0;
        var mediaType = DocumentSniffer.Sniff(stream);
        return mediaType switch
        {
            DocumentSniffer.Pdf => ClassifyPdf(StreamBytes.Read(stream), ct),
            DocumentSniffer.Xlsx => Office(stream, DocumentKind.Spreadsheet, mediaType, DocumentRoutes.Spreadsheet, "Excel workbook; convert sheets to markdown tables"),
            DocumentSniffer.Docx => Office(stream, DocumentKind.WordDocument, mediaType, DocumentRoutes.Text, "Word document; convert to markdown"),
            _ when DocumentSniffer.IsImage(mediaType) => ClassifyImage(StreamBytes.Read(stream), mediaType),
            DocumentSniffer.Ole => throw new DocumentException(
                "legacy (.xls/.doc) or password-protected Office files are not supported; save as unprotected .xlsx/.docx",
                StatusCodes.Status415UnsupportedMediaType),
            _ => throw new DocumentException(Unsupported, StatusCodes.Status415UnsupportedMediaType)
        };
    }

    private DocumentClassification ClassifyPdf(byte[] bytes, CancellationToken ct)
    {
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            if (pdf.NumberOfPages > _options.MaxPdfPages)
                throw new DocumentException($"PDF has {pdf.NumberOfPages} pages; at most {_options.MaxPdfPages} are supported",
                    StatusCodes.Status422UnprocessableEntity);
            var inspection = new PdfInspection(pdf, bytes, _options);

            foreach (var detector in PdfDetectors) detector.InspectDocument(inspection);
            // One pass over the pages so each is parsed once, however many detectors look at it.
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
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
        catch (Exception ex) when (ex is not (DocumentException or OperationCanceledException))
        {
            throw new DocumentException("PDF could not be read", StatusCodes.Status422UnprocessableEntity, ex);
        }
    }

    private DocumentClassification ClassifyImage(byte[] bytes, string mediaType)
    {
        IReadOnlyList<RasterPage> pages;
        try
        {
            pages = RasterImages.Inspect(bytes, mediaType, _options.MaxImageFrames + 1);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DocumentException("image could not be read", StatusCodes.Status422UnprocessableEntity, ex);
        }
        if (pages.Count > _options.MaxImageFrames)
            throw new DocumentException($"image has more than {_options.MaxImageFrames} pages", StatusCodes.Status422UnprocessableEntity);

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

    private DocumentClassification Office(Stream stream, DocumentKind kind, string mediaType, string route, string reason)
    {
        CheckPackageSize(stream);
        return new()
        {
            Kind = kind,
            Strategy = PreparationStrategy.Markdown,
            Route = route,
            Reason = reason,
            MediaType = mediaType
        };
    }

    /// <summary>
    /// Rejects zip bombs before a converter loads the package. Declared sizes are enough: ZipArchive stops reading
    /// an entry at its declared size, so an entry that lies about it is truncated rather than expanded.
    /// </summary>
    private void CheckPackageSize(Stream stream)
    {
        var max = _options.MaxOfficeUncompressedBytes;
        stream.Position = 0;
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                // Compared as "size > max - total" so huge declared sizes cannot overflow past the check.
                if (entry.Length > max - total)
                    throw new DocumentException($"document expands to more than {max} bytes when unpacked",
                        StatusCodes.Status422UnprocessableEntity);
                total += entry.Length;
            }
        }
        finally
        {
            stream.Position = 0;
        }
    }
}

using System.IO.Compression;
using LayaSample.Api.Models;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms;
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
    private readonly DocumentAnalysisOptions _options = options.Value;

    public DocumentClassification Classify(Stream stream)
    {
        stream.Position = 0;
        var head = new byte[1024];
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = 0;

        if (head.AsSpan(0, read).IndexOf("%PDF-"u8) >= 0)
            return ClassifyPdf(stream);
        if (read >= 4 && head[0] == 'P' && head[1] == 'K')
            return ClassifyOpenXml(stream);

        throw new DocumentException("unsupported document type; expected PDF, .xlsx or .docx", StatusCodes.Status415UnsupportedMediaType);
    }

    private DocumentClassification ClassifyPdf(Stream stream)
    {
        try
        {
            using var pdf = PdfDocument.Open(stream);

            int fields = 0, filled = 0;
            if (pdf.TryGetForm(out var form) && form is not null)
            {
                foreach (var field in form.GetFields())
                {
                    fields++;
                    if (HasValue(field.GetFieldValue())) filled++;
                }
            }

            int pages = pdf.NumberOfPages, images = 0, sparsePages = 0;
            long chars = 0;
            foreach (var page in pdf.GetPages())
            {
                var pageChars = page.Letters.Count;
                chars += pageChars;
                if (pageChars < _options.MinCharsPerPage) sparsePages++;
                images += page.GetImages().Count();
            }
            var avg = pages == 0 ? 0 : (double)chars / pages;

            DocumentClassification Result(DocumentKind kind, PreparationStrategy strategy, string route, string reason) =>
                new(kind, strategy, route, reason, pages, fields, filled, images, Math.Round(avg, 1));

            // Filled values live in form fields (often only in the appearance stream), not the page content,
            // so text extraction misses them. Rendering to PNG with form fill is the only reliable way to show them.
            if (fields > 0)
                return Result(DocumentKind.FormPdf, PreparationStrategy.RenderToPng, DocumentRoutes.Form,
                    $"PDF has {fields} form field(s), {filled} filled; render to PNG so field values are visible");
            if (pages > 0 && sparsePages == pages)
                return Result(DocumentKind.ScannedPdf, PreparationStrategy.RenderToPng, DocumentRoutes.Vision,
                    "no usable text layer on any page (scanned or image-only)");
            if (sparsePages > 0)
                return Result(DocumentKind.MixedPdf, PreparationStrategy.RenderToPng, DocumentRoutes.Vision,
                    $"{sparsePages} of {pages} page(s) have no usable text layer");
            return Result(DocumentKind.TextPdf, PreparationStrategy.Markdown, DocumentRoutes.Text,
                "text layer present on every page and no form fields");
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new DocumentException("PDF is password protected", StatusCodes.Status422UnprocessableEntity, ex);
        }
        catch (Exception ex) when (ex is UglyToad.PdfPig.Core.PdfDocumentFormatException or InvalidOperationException or ArgumentException)
        {
            throw new DocumentException("PDF could not be read", StatusCodes.Status422UnprocessableEntity, ex);
        }
    }

    private static DocumentClassification ClassifyOpenXml(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.GetEntry("xl/workbook.xml") is not null)
                return new(DocumentKind.Spreadsheet, PreparationStrategy.Markdown, DocumentRoutes.Spreadsheet,
                    "Excel workbook; convert sheets to markdown tables", 0, 0, 0, 0, 0);
            if (zip.GetEntry("word/document.xml") is not null)
                return new(DocumentKind.WordDocument, PreparationStrategy.Markdown, DocumentRoutes.Text,
                    "Word document; convert to markdown", 0, 0, 0, 0, 0);
        }
        catch (InvalidDataException ex)
        {
            throw new DocumentException("document could not be read", StatusCodes.Status422UnprocessableEntity, ex);
        }

        throw new DocumentException("unsupported document type; expected PDF, .xlsx or .docx", StatusCodes.Status415UnsupportedMediaType);
    }

    private static bool HasValue(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => !string.IsNullOrWhiteSpace(s) && s != "Off",
        System.Collections.IEnumerable e => e.Cast<object?>().Any(HasValue),
        _ => !string.IsNullOrWhiteSpace(value.ToString())
    };
}

using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents.Images;
using LayaSample.Api.Services.Documents.Ocr;
using LayaSample.Api.Services.Documents.Pdf;
using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace LayaSample.Api.Services.Documents;

public interface IDocumentPreparer
{
    /// <summary>Converts the document into the form the downstream agent should receive.</summary>
    /// <exception cref="DocumentException">The strategy does not apply to this kind of document (400), or its content is unreadable (422).</exception>
    PreparedDocument Prepare(Stream stream, DocumentClassification classification, PreparationStrategy strategy, CancellationToken ct = default);
}

public sealed class DocumentPreparer(IOptions<DocumentAnalysisOptions> options, IDocumentClassifier classifier, IOcrEngine ocr) : IDocumentPreparer
{
    private readonly DocumentAnalysisOptions _options = options.Value;

    public PreparedDocument Prepare(Stream stream, DocumentClassification classification, PreparationStrategy strategy, CancellationToken ct = default)
    {
        Validate(classification.Kind, strategy);
        try
        {
            return new PreparedDocument(strategy, PrepareParts(StreamBytes.Read(stream), classification, strategy, includeAttachments: true, ct));
        }
        // The container was valid enough to classify, but the libraries reject its content.
        catch (Exception ex) when (ex is not (DocumentException or OperationCanceledException or ArgumentOutOfRangeException))
        {
            throw new DocumentException("document content could not be read", StatusCodes.Status422UnprocessableEntity, ex);
        }
    }

    private static void Validate(DocumentKind kind, PreparationStrategy strategy)
    {
        var error = strategy switch
        {
            PreparationStrategy.RenderToPng or PreparationStrategy.Hybrid when !kind.IsPaged() =>
                $"{strategy} only applies to PDF and image documents",
            PreparationStrategy.Markdown when kind == DocumentKind.Image =>
                "Markdown does not apply to images; use Hybrid for page images with OCR text",
            PreparationStrategy.StructuredData or PreparationStrategy.PerPage when !kind.IsPdf() =>
                $"{strategy} only applies to PDF documents",
            _ => null
        };
        if (error is not null) throw new DocumentException(error, StatusCodes.Status400BadRequest);
    }

    private List<PreparedPart> PrepareParts(byte[] bytes, DocumentClassification classification, PreparationStrategy strategy, bool includeAttachments, CancellationToken ct)
    {
        if (strategy == PreparationStrategy.AsIs)
            return [new PreparedPart(PartRole.Original, classification.MediaType, null, null, bytes)];

        return classification.Kind switch
        {
            DocumentKind.Spreadsheet => [Markdown(null, SpreadsheetToMarkdown(new MemoryStream(bytes, writable: false), ct))],
            DocumentKind.WordDocument => [Markdown(null, WordToMarkdown(new MemoryStream(bytes, writable: false)))],
            DocumentKind.Image => PrepareImage(bytes, classification, strategy, ct),
            _ => PreparePdf(bytes, classification, strategy, includeAttachments, ct)
        };
    }

    private List<PreparedPart> PrepareImage(byte[] bytes, DocumentClassification classification, PreparationStrategy strategy, CancellationToken ct)
    {
        var parts = new List<PreparedPart>();
        foreach (var page in classification.Pages.Take(_options.MaxPages))
        {
            ct.ThrowIfCancellationRequested();
            using var bitmap = RasterImages.Decode(bytes, classification.MediaType, page.Number - 1);
            parts.Add(Png(bitmap, page.Number));
            if (strategy == PreparationStrategy.Hybrid && Ocr(bitmap, page.Number, ct) is { } text) parts.Add(text);
        }
        return parts;
    }

    private List<PreparedPart> PreparePdf(byte[] bytes, DocumentClassification classification, PreparationStrategy strategy, bool includeAttachments, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(bytes);
        var parts = new List<PreparedPart>();

        // StructuredData skips the pages entirely (for dynamic XFA they are only a viewer placeholder).
        IReadOnlyList<PageClassification> pages = strategy == PreparationStrategy.StructuredData ? [] : classification.Pages;
        var rendered = 0;
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            var pageStrategy = strategy == PreparationStrategy.PerPage ? page.Strategy : strategy;
            if (pageStrategy is PreparationStrategy.Markdown)
            {
                parts.Add(Markdown(page.Number, $"## Page {page.Number}\n\n{pdf.GetPage(page.Number).Text.Trim()}\n"));
                continue;
            }
            if (pageStrategy is not (PreparationStrategy.RenderToPng or PreparationStrategy.Hybrid)) continue;
            if (rendered++ >= _options.MaxPages) break;

            using var bitmap = Render(bytes, page);
            parts.Add(Png(bitmap, page.Number));
            if (pageStrategy == PreparationStrategy.Hybrid && HybridText(pdf, page, bitmap, ct) is { } text) parts.Add(text);
        }

        // Machine-readable data complements the pages whatever the strategy: field values are exact where a render is not.
        if (PdfStructuredData.ReadFormValuesJson(pdf) is { } formValues)
            parts.Add(new PreparedPart(PartRole.StructuredData, DocumentSniffer.Json, null, formValues, null));
        if (classification.IsXfa && PdfStructuredData.ReadXfaDatasets(pdf) is { } datasets)
            parts.Add(new PreparedPart(PartRole.StructuredData, DocumentSniffer.Xml, null, datasets, null));
        if (includeAttachments)
        {
            foreach (var (name, data) in PdfStructuredData.ReadAttachments(pdf).Take(_options.MaxAttachments))
                parts.AddRange(PrepareAttachment(name, data, structuredOnly: strategy == PreparationStrategy.StructuredData, ct));
        }

        if (strategy == PreparationStrategy.StructuredData && parts.Count == 0)
            throw new DocumentException("PDF has no machine-readable data (form values, XFA datasets or embedded XML/JSON)",
                StatusCodes.Status422UnprocessableEntity);
        return parts;
    }

    /// <summary>
    /// Textual attachments (e-invoice XML, JSON) are passed through; embedded documents are classified and prepared
    /// with their own default strategy. Attachments that cannot be prepared are still listed in the classification.
    /// </summary>
    private IEnumerable<PreparedPart> PrepareAttachment(string name, byte[] bytes, bool structuredOnly, CancellationToken ct)
    {
        var mediaType = DocumentSniffer.Sniff(bytes);
        if (DocumentSniffer.IsTextual(mediaType))
            return [new PreparedPart(PartRole.StructuredData, mediaType, null, Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), null, name)];
        if (structuredOnly) return [];

        try
        {
            var classification = classifier.Classify(new MemoryStream(bytes, writable: false));
            return PrepareParts(bytes, classification, classification.Strategy, includeAttachments: false, ct)
                .Select(p => p with { Source = name });
        }
        catch (DocumentException)
        {
            return [];
        }
    }

    private SKBitmap Render(byte[] bytes, PageClassification page)
    {
        // Render what a person sees: WithFormFill draws AcroForm field values, WithAnnotations draws
        // Fill & Sign text, signatures and stamps. Without them a filled-in PDF renders blank.
        var render = new RenderOptions(Dpi: page.RenderDpi ?? _options.PngDpi, WithAnnotations: true, WithFormFill: true);
        return Conversion.ToImage(new MemoryStream(bytes, writable: false), page: page.Number - 1, options: render);
    }

    /// <summary>The text layer when it is usable (exact), otherwise OCR of the rendered page.</summary>
    private PreparedPart? HybridText(PdfDocument pdf, PageClassification page, SKBitmap bitmap, CancellationToken ct)
    {
        if (page.Content is PageContent.Scanned or PageContent.Fax or PageContent.BrokenText)
            return Ocr(bitmap, page.Number, ct);

        var text = pdf.GetPage(page.Number).Text.Trim();
        return text.Length == 0 ? null : new PreparedPart(PartRole.PageText, DocumentSniffer.Text, page.Number, text, null);
    }

    private PreparedPart? Ocr(SKBitmap bitmap, int page, CancellationToken ct)
    {
        if (!_options.EnableOcr) return null;
        var text = ocr.Recognize(bitmap, ct);
        return text.Length == 0 ? null : new PreparedPart(PartRole.OcrText, DocumentSniffer.Text, page, text, null);
    }

    private static PreparedPart Png(SKBitmap bitmap, int page)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new PreparedPart(PartRole.PageImage, DocumentSniffer.Png, page, null, data.ToArray());
    }

    private static PreparedPart Markdown(int? page, string text) => new(PartRole.PageText, "text/markdown", page, text, null);

    private string SpreadsheetToMarkdown(Stream stream, CancellationToken ct)
    {
        using var workbook = new XLWorkbook(stream);
        var sb = new StringBuilder();
        foreach (var sheet in workbook.Worksheets)
        {
            ct.ThrowIfCancellationRequested();
            sb.Append("## ").AppendLine(sheet.Name).AppendLine();
            var range = sheet.RangeUsed();
            if (range is null) { sb.AppendLine("_empty sheet_").AppendLine(); continue; }

            var total = range.RowCount();
            var rows = range.Rows().Take(_options.MaxSpreadsheetRows)
                .Select(r => r.Cells().Select(c => c.GetFormattedString()).ToList()).ToList();
            AppendTable(sb, rows);
            if (total > rows.Count)
                sb.AppendLine($"_truncated: showing {rows.Count} of {total} rows_").AppendLine();
        }
        return sb.ToString();
    }

    private static string WordToMarkdown(Stream stream)
    {
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var sb = new StringBuilder();
        var body = doc.MainDocumentPart?.Document?.Body
            ?? throw new DocumentException("Word document has no body", StatusCodes.Status422UnprocessableEntity);
        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case Paragraph p when !string.IsNullOrWhiteSpace(p.InnerText):
                    sb.AppendLine($"{HeadingPrefix(p)}{p.InnerText.Trim()}").AppendLine();
                    break;
                case Table t:
                    AppendTable(sb, t.Elements<TableRow>()
                        .Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()).ToList());
                    break;
            }
        }
        return sb.ToString();
    }

    private static string HeadingPrefix(Paragraph p)
    {
        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (style is not null && style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(style["Heading".Length..], out var level))
            return new string('#', Math.Clamp(level, 1, 6)) + " ";
        return style is "Title" ? "# " : "";
    }

    private static void AppendTable(StringBuilder sb, List<List<string>> rows)
    {
        if (rows.Count == 0) return;
        var width = rows.Max(r => r.Count);
        static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ").Trim();
        string Row(List<string> r) => "| " + string.Join(" | ", Enumerable.Range(0, width).Select(i => i < r.Count ? Cell(r[i]) : "")) + " |";

        sb.AppendLine(Row(rows[0]));
        sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", width)));
        foreach (var row in rows.Skip(1)) sb.AppendLine(Row(row));
        sb.AppendLine();
    }
}

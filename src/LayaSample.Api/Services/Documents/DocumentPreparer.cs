using System.Text;
using System.Xml;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents.Pdf;
using LayaSample.Rendering;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace LayaSample.Api.Services.Documents;

public interface IDocumentPreparer
{
    /// <summary>
    /// Converts the document into the form the downstream agent should receive, using the strategy the classification
    /// chose (per page for PDFs).
    /// </summary>
    /// <exception cref="DocumentException">The content is unreadable (422).</exception>
    /// <exception cref="OcrUnavailableException"/>
    /// <exception cref="RendererUnavailableException"/>
    /// <exception cref="RenderingFailedException"/>
    Task<PreparedDocument> PrepareAsync(Stream stream, DocumentClassification classification, CancellationToken ct = default);
}

/// <summary>
/// Prepares documents. Page images and OCR come from <see cref="IPageRasterizer"/> (the renderer service in
/// production); text layers, form data and Office conversion are read here with managed libraries.
/// </summary>
public sealed class DocumentPreparer(
    IOptions<DocumentAnalysisOptions> options, IDocumentClassifier classifier, IPageRasterizer rasterizer, ILogger<DocumentPreparer> logger)
    : IDocumentPreparer
{
    private readonly DocumentAnalysisOptions _options = options.Value;

    /// <summary>Page renders left for one request, shared by the document and its embedded documents.</summary>
    private sealed class Budget(int renders)
    {
        private int _renders = renders;

        public bool TryRender()
        {
            if (_renders == 0) return false;
            _renders--;
            return true;
        }
    }

    public async Task<PreparedDocument> PrepareAsync(Stream stream, DocumentClassification classification, CancellationToken ct = default)
    {
        try
        {
            var budget = new Budget(_options.MaxPages);
            return new PreparedDocument(classification.Strategy,
                await PreparePartsAsync(StreamBytes.Read(stream), classification, budget, includeAttachments: true, ct));
        }
        // The container was valid enough to classify, but the libraries (or the renderer) reject its content.
        catch (Exception ex) when (!IsServerProblem(ex) && ex is not DocumentException)
        {
            throw new DocumentException("document content could not be read", StatusCodes.Status422UnprocessableEntity, ex);
        }
    }

    /// <summary>Failures that say nothing about the document, so they must not turn into 422 or be skipped.</summary>
    private static bool IsServerProblem(Exception ex) =>
        ex is OperationCanceledException or OcrUnavailableException or RendererUnavailableException or RenderingFailedException;

    private async Task<List<PreparedPart>> PreparePartsAsync(byte[] bytes, DocumentClassification classification, Budget budget,
        bool includeAttachments, CancellationToken ct)
    {
        var strategy = classification.Strategy;
        return classification.Kind switch
        {
            DocumentKind.Spreadsheet => [Markdown(null, SpreadsheetToMarkdown(new MemoryStream(bytes, writable: false), ct))],
            DocumentKind.WordDocument => [Markdown(null, WordToMarkdown(new MemoryStream(bytes, writable: false)))],
            DocumentKind.Image => await PrepareImageAsync(bytes, classification, strategy, budget, ct),
            _ => await PreparePdfAsync(bytes, classification, strategy, budget, includeAttachments, ct)
        };
    }

    private async Task<List<PreparedPart>> PrepareImageAsync(byte[] bytes, DocumentClassification classification, PreparationStrategy strategy,
        Budget budget, CancellationToken ct)
    {
        var requests = new List<PageRequest>();
        foreach (var page in classification.Pages)
        {
            if (!budget.TryRender()) break;
            requests.Add(new PageRequest(page.Number, Grayscale: page.Content == PageContent.Fax,
                Ocr: strategy == PreparationStrategy.Hybrid && _options.EnableOcr));
        }

        var parts = new List<PreparedPart>();
        foreach (var page in await RenderAsync(bytes, classification.MediaType, requests, ct))
        {
            parts.Add(PageImage(page));
            if (OcrText(page) is { } text) parts.Add(text);
        }
        return parts;
    }

    private async Task<List<PreparedPart>> PreparePdfAsync(byte[] bytes, DocumentClassification classification, PreparationStrategy strategy,
        Budget budget, bool includeAttachments, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(bytes);

        // First decide each page: markdown from the text layer here, or a render (with OCR where the text layer is
        // missing or unusable). All renders then go to the rasterizer as one job.
        var slots = new List<(PageClassification Page, PreparationStrategy Strategy, PreparedPart? Markdown)>();
        var requests = new List<PageRequest>();
        // StructuredData skips the pages entirely (for dynamic XFA they are only a viewer placeholder).
        IReadOnlyList<PageClassification> pages = strategy == PreparationStrategy.StructuredData ? [] : classification.Pages;
        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            var pageStrategy = strategy == PreparationStrategy.PerPage ? page.Strategy : strategy;
            if (pageStrategy is PreparationStrategy.Markdown)
            {
                slots.Add((page, pageStrategy, Markdown(page.Number, $"## Page {page.Number}\n\n{pdf.GetPage(page.Number).Text.Trim()}\n")));
                continue;
            }
            // The budget applies to renders only; markdown pages after it are still included.
            if (pageStrategy is not (PreparationStrategy.RenderToPng or PreparationStrategy.Hybrid) || !budget.TryRender()) continue;

            slots.Add((page, pageStrategy, null));
            requests.Add(new PageRequest(page.Number, page.RenderDpi ?? _options.PngDpi, Grayscale: page.Content == PageContent.Fax,
                Ocr: pageStrategy == PreparationStrategy.Hybrid && NeedsOcr(page) && _options.EnableOcr));
        }

        var rendered = (await RenderAsync(bytes, DocumentSniffer.Pdf, requests, ct)).ToDictionary(p => p.Number);
        var parts = new List<PreparedPart>();
        foreach (var (page, pageStrategy, markdown) in slots)
        {
            if (markdown is not null)
            {
                parts.Add(markdown);
                continue;
            }
            if (!rendered.TryGetValue(page.Number, out var image))
                throw new RenderingFailedException($"renderer did not return page {page.Number}");

            parts.Add(PageImage(image));
            if (pageStrategy != PreparationStrategy.Hybrid) continue;
            // The text layer when it is usable (exact), otherwise the OCR text.
            var text = NeedsOcr(page) ? OcrText(image) : TextLayer(pdf, page.Number);
            if (text is not null) parts.Add(text);
        }

        // Machine-readable data complements the pages whatever the strategy: field values are exact where a render is not.
        if (PdfStructuredData.ReadFormValuesJson(pdf) is { } formValues)
            parts.Add(new PreparedPart(PartRole.StructuredData, DocumentSniffer.Json, null, formValues, null));
        if (classification.IsXfa && PdfStructuredData.ReadXfaDatasets(pdf) is { } datasets)
            parts.Add(new PreparedPart(PartRole.StructuredData, DocumentSniffer.Xml, null, datasets, null));
        if (includeAttachments)
        {
            foreach (var (name, data) in PdfStructuredData.ReadAttachments(pdf).Take(_options.MaxAttachments))
                parts.AddRange(await PrepareAttachmentAsync(name, data.ToArray(), structuredOnly: strategy == PreparationStrategy.StructuredData, budget, ct));
        }

        if (strategy == PreparationStrategy.StructuredData && parts.Count == 0)
            throw new DocumentException("PDF has no machine-readable data (form values, XFA datasets or embedded XML/JSON)",
                StatusCodes.Status422UnprocessableEntity);
        return parts;
    }

    private static bool NeedsOcr(PageClassification page) => page.Content is PageContent.Scanned or PageContent.Fax or PageContent.BrokenText;

    private async Task<IReadOnlyList<RenderedPage>> RenderAsync(byte[] bytes, string mediaType, List<PageRequest> requests, CancellationToken ct) =>
        requests.Count == 0 ? [] : await rasterizer.RenderAsync(new RenderJob(bytes, mediaType, requests), ct);

    /// <summary>
    /// Textual attachments (e-invoice XML, JSON) are passed through; embedded documents are classified and prepared
    /// with their own default strategy. Attachments that cannot be prepared are still listed in the classification.
    /// </summary>
    private async Task<IEnumerable<PreparedPart>> PrepareAttachmentAsync(string name, byte[] bytes, bool structuredOnly, Budget budget,
        CancellationToken ct)
    {
        var mediaType = DocumentSniffer.Sniff(bytes);
        if (DocumentSniffer.IsTextual(mediaType))
            return [new PreparedPart(PartRole.StructuredData, mediaType, null, DecodeText(bytes, mediaType), null, name)];
        if (structuredOnly) return [];

        try
        {
            var classification = await classifier.ClassifyAsync(new MemoryStream(bytes, writable: false), ct);
            return (await PreparePartsAsync(bytes, classification, budget, includeAttachments: false, ct))
                .Select(p => p with { Source = name });
        }
        // A corrupt embedded file must not fail the document that carries it; it stays listed in the classification.
        // That includes one that crashes the renderer, but not an unreachable renderer or missing OCR models.
        catch (Exception ex) when (ex is RenderingFailedException || !IsServerProblem(ex))
        {
            logger.LogWarning(ex, "Embedded file {Attachment} could not be prepared and was skipped", name);
            return [];
        }
    }

    /// <summary>XML is decoded with the encoding it declares (UTF-16, ISO-8859-1, ...); everything else as UTF-8.</summary>
    private static string DecodeText(byte[] bytes, string mediaType)
    {
        if (mediaType == DocumentSniffer.Xml)
        {
            try
            {
                using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false),
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                return System.Xml.Linq.XDocument.Load(reader, System.Xml.Linq.LoadOptions.PreserveWhitespace)
                    .ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
            }
            // Not well-formed: pass the text through for the agent to make what it can of it.
            catch (XmlException) { }
        }
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    private static PreparedPart? TextLayer(PdfDocument pdf, int page)
    {
        var text = pdf.GetPage(page).Text.Trim();
        return text.Length == 0 ? null : new PreparedPart(PartRole.PageText, DocumentSniffer.Text, page, text, null);
    }

    private static PreparedPart PageImage(RenderedPage page) => new(PartRole.PageImage, DocumentSniffer.Png, page.Number, null, page.Png);

    private static PreparedPart? OcrText(RenderedPage page) =>
        page.OcrText is { Length: > 0 } text ? new PreparedPart(PartRole.OcrText, DocumentSniffer.Text, page.Number, text, null) : null;

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
        AppendBlocks(sb, body.ChildElements);
        return sb.ToString();
    }

    private static void AppendBlocks(StringBuilder sb, IEnumerable<OpenXmlElement> elements)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                // InnerText includes text boxes anchored in the paragraph.
                case Paragraph p:
                    if (!string.IsNullOrWhiteSpace(p.InnerText)) sb.AppendLine($"{Prefix(p)}{p.InnerText.Trim()}").AppendLine();
                    break;
                case Table t:
                    AppendTable(sb, t.Elements<TableRow>()
                        .Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToList()).ToList());
                    break;
                // Content controls (common in templates and forms) and custom XML wrap ordinary paragraphs and tables.
                default:
                    AppendBlocks(sb, element.ChildElements);
                    break;
            }
        }
    }

    private static string Prefix(Paragraph p)
    {
        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (style is not null && style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(style["Heading".Length..], out var level))
            return new string('#', Math.Clamp(level, 1, 6)) + " ";
        if (style is "Title") return "# ";
        // Bulleted and numbered lists look alike without resolving the numbering definitions; both become bullets.
        return p.ParagraphProperties?.NumberingProperties is not null ? "- " : "";
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

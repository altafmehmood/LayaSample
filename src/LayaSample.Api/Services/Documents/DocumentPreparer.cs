using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LayaSample.Api.Models;
using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace LayaSample.Api.Services.Documents;

public interface IDocumentPreparer
{
    /// <summary>Converts the document into the form the downstream agent should receive.</summary>
    /// <exception cref="DocumentException">The strategy does not apply to this kind of document (400).</exception>
    PreparedDocument Prepare(Stream stream, DocumentClassification classification, PreparationStrategy strategy);
}

public sealed class DocumentPreparer(IOptions<DocumentAnalysisOptions> options) : IDocumentPreparer
{
    private readonly DocumentAnalysisOptions _options = options.Value;

    public PreparedDocument Prepare(Stream stream, DocumentClassification classification, PreparationStrategy strategy)
    {
        stream.Position = 0;
        var parts = strategy switch
        {
            PreparationStrategy.AsIs => AsIs(stream, classification.Kind),
            PreparationStrategy.RenderToPng => IsPdf(classification.Kind)
                ? RenderToPng(stream)
                : throw new DocumentException("RenderToPng only applies to PDF documents", StatusCodes.Status400BadRequest),
            PreparationStrategy.Markdown => ToMarkdown(stream, classification.Kind),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy))
        };
        return new PreparedDocument(strategy, parts);
    }

    private static bool IsPdf(DocumentKind kind) =>
        kind is DocumentKind.FormPdf or DocumentKind.TextPdf or DocumentKind.ScannedPdf or DocumentKind.MixedPdf;

    private static IReadOnlyList<PreparedPart> AsIs(Stream stream, DocumentKind kind)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var mediaType = kind switch
        {
            DocumentKind.Spreadsheet => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            DocumentKind.WordDocument => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/pdf"
        };
        return [new PreparedPart(mediaType, null, null, ms.ToArray())];
    }

    private IReadOnlyList<PreparedPart> RenderToPng(Stream stream)
    {
        var parts = new List<PreparedPart>();
        // WithFormFill draws the filled-in field values onto the page; without it a form PDF renders blank.
        var render = new RenderOptions(Dpi: _options.PngDpi, WithFormFill: true);
        var page = 0;
        foreach (var bitmap in Conversion.ToImages(stream, leaveOpen: true, options: render))
        {
            using (bitmap)
            {
                page++;
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                parts.Add(new PreparedPart("image/png", page, null, data.ToArray()));
            }
            if (page >= _options.MaxPages) break;
        }
        return parts;
    }

    private static IReadOnlyList<PreparedPart> ToMarkdown(Stream stream, DocumentKind kind) => kind switch
    {
        DocumentKind.Spreadsheet => [new PreparedPart("text/markdown", null, SpreadsheetToMarkdown(stream), null)],
        DocumentKind.WordDocument => [new PreparedPart("text/markdown", null, WordToMarkdown(stream), null)],
        _ => PdfToMarkdown(stream)
    };

    private static IReadOnlyList<PreparedPart> PdfToMarkdown(Stream stream)
    {
        using var pdf = PdfDocument.Open(stream);
        return pdf.GetPages()
            .Select(p => new PreparedPart("text/markdown", p.Number, $"## Page {p.Number}\n\n{p.Text.Trim()}\n", null))
            .ToList();
    }

    private static string SpreadsheetToMarkdown(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sb = new StringBuilder();
        foreach (var sheet in workbook.Worksheets)
        {
            sb.Append("## ").AppendLine(sheet.Name).AppendLine();
            var range = sheet.RangeUsed();
            if (range is null) { sb.AppendLine("_empty sheet_").AppendLine(); continue; }

            var rows = range.Rows().Select(r => r.Cells().Select(c => c.GetFormattedString()).ToList()).ToList();
            AppendTable(sb, rows);
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

using System.Text;

namespace GarakShim;

/// <summary>A small text PDF, written by hand so the shim needs no library: Helvetica, wrapped lines, as many pages as the text takes.</summary>
public static class PdfText
{
    public const int CharsPerLine = 92;
    private const int LinesPerPage = 52;

    /// <summary>The title and header lines, then the body, wrapped. Text outside Latin-1 becomes '?'.</summary>
    public static byte[] Build(string title, IReadOnlyList<string> header, string body)
    {
        var lines = new List<string> { title, "" };
        lines.AddRange(header);
        lines.Add("");
        lines.AddRange(Wrap(body));

        var pages = lines.Chunk(LinesPerPage).ToList();
        // 1 catalog, 2 pages, 3 font, then a page object and its content stream per page.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", pages.Select((_, i) => $"{4 + 2 * i} 0 R"))}] /Count {pages.Count} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
        };
        for (var i = 0; i < pages.Count; i++)
        {
            var content = new StringBuilder("BT /F1 10 Tf 12 TL 50 800 Td\n");
            foreach (var line in pages[i])
                content.Append('(').Append(Escape(line)).Append(") Tj T*\n");
            content.Append("ET");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents {5 + 2 * i} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        // Every character is one Latin-1 byte, so offsets and lengths can be counted in characters.
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    /// <summary>Lines of at most <see cref="CharsPerLine"/> characters, broken at spaces where there are any.</summary>
    public static IEnumerable<string> Wrap(string text)
    {
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var rest = paragraph;
            if (rest.Length == 0)
            {
                yield return "";
                continue;
            }
            while (rest.Length > CharsPerLine)
            {
                var cut = rest.LastIndexOf(' ', CharsPerLine);
                if (cut <= 0) cut = CharsPerLine;
                yield return rest[..cut].TrimEnd();
                rest = rest[cut..].TrimStart();
            }
            yield return rest;
        }
    }

    private static string Escape(string line)
    {
        var text = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (c is '\\' or '(' or ')') text.Append('\\').Append(c);
            else if (c < 32 || c == 127) text.Append(' ');
            else text.Append(c > 255 ? '?' : c);
        }
        return text.ToString();
    }
}

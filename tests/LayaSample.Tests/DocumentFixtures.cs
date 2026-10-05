using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Writer;

namespace LayaSample.Tests;

/// <summary>Builds small sample documents in code so no binary fixtures are checked in.</summary>
internal static class DocumentFixtures
{
    private const string LongText =
        "This agreement describes the terms under which the supplier delivers goods to the customer.";

    public static byte[] TextPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        for (var i = 0; i < 2; i++)
            builder.AddPage(595, 842).AddText(LongText, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        return builder.Build();
    }

    /// <summary>A page with no text layer at all, as a scan would be.</summary>
    public static byte[] BlankPdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(595, 842);
        return builder.Build();
    }

    public static byte[] MixedPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        builder.AddPage(595, 842).AddText(LongText, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        builder.AddPage(595, 842);
        return builder.Build();
    }

    /// <summary>One AcroForm text field whose value is held in the field and its appearance stream.</summary>
    public static byte[] FilledFormPdf(string value = "Jane Doe")
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [4 0 R] /Resources << /Font << /F1 7 0 R >> >> >>",
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V ({value}) /Rect [50 700 250 720] /P 3 0 R /F 4 /DA (/F1 12 Tf 0 g) /AP << /N 6 0 R >> >>",
            "<< /Fields [4 0 R] /DA (/F1 12 Tf 0 g) /DR << /Font << /F1 7 0 R >> >> >>",
            Stream($"<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /F1 7 0 R >> >> ", $"/Tx BMC q BT /F1 12 Tf 0 g 2 6 Td ({value}) Tj ET Q EMC"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) pdf.Append($"{o:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());

        static string Stream(string dictPrefix, string content) =>
            $"{dictPrefix}/Length {content.Length} >>\nstream\n{content}\nendstream";
    }

    public static byte[] Xlsx()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Orders");
        ws.Cell("A1").Value = "Item";
        ws.Cell("B1").Value = "Qty";
        ws.Cell("A2").Value = "Widget";
        ws.Cell("B2").Value = 3;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static byte[] Docx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Quarterly report"))),
                new Paragraph(new Run(new Text("Revenue grew this quarter.")))));
        }
        return ms.ToArray();
    }
}

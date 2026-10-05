using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ImageMagick;
using ImageMagick.Drawing;
using SkiaSharp;
using UglyToad.PdfPig.Writer;

namespace LayaSample.Tests;

/// <summary>Builds small sample documents in code so no binary fixtures are checked in.</summary>
public static class DocumentFixtures
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
    public static byte[] FilledFormPdf(string value = "Jane Doe") => RawPdf(
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [4 0 R] /Resources << /Font << /F1 7 0 R >> >> >>",
        $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V ({value}) /Rect [50 700 250 720] /P 3 0 R /F 4 /DA (/F1 12 Tf 0 g) /AP << /N 6 0 R >> >>",
        "<< /Fields [4 0 R] /DA (/F1 12 Tf 0 g) /DR << /Font << /F1 7 0 R >> >> >>",
        $"/Tx BMC q BT /F1 12 Tf 0 g 2 6 Td ({value}) Tj ET Q EMC");

    /// <summary>
    /// A normal text page that was then "filled" with a FreeText annotation (as Acrobat / Preview Fill &amp; Sign do).
    /// The value is not in the page's text layer.
    /// </summary>
    public static byte[] AnnotatedPdf(string value = "Signed John Smith") => RawPdf(
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [4 0 R] /Contents 5 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
        $"<< /Type /Annot /Subtype /FreeText /Contents ({value}) /Rect [50 600 250 620] /P 3 0 R /F 4 /DA (/F1 12 Tf 0 g) /AP << /N 6 0 R >> >>",
        StreamObject("<< ", $"BT /F1 12 Tf 50 700 Td ({LongText}) Tj ET"),
        $"q BT /F1 12 Tf 0 g 2 6 Td ({value}) Tj ET Q",
        catalogExtra: "");

    /// <summary>
    /// Hand-assembled single-page PDF: 1 catalog, 2 pages, 3 page, 4 annotation, 5 extra object,
    /// 6 the annotation's appearance stream, 7 Helvetica.
    /// </summary>
    private static byte[] RawPdf(string page, string annotation, string extra, string appearance, string catalogExtra = "/AcroForm 5 0 R ") =>
        BuildPdf(
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra}>>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            page,
            annotation,
            extra,
            StreamObject("<< /Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /F1 7 0 R >> >> ", appearance),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    /// <summary>
    /// A form whose fields share short names under different parents (buyer.name, seller.name), plus a radio group and
    /// a checkbox: 1 catalog, 2 pages, 3 page, 4 font, 5 AcroForm, 6/8 parents, 7/9 text widgets, 10 radio group,
    /// 11/12 radio widgets, 13 checkbox, 14 shared appearance.
    /// </summary>
    public static byte[] NestedFormPdf() => BuildPdf(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [7 0 R 9 0 R 11 0 R 12 0 R 13 0 R] /Resources << /Font << /F1 4 0 R >> >> >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Fields [6 0 R 8 0 R 10 0 R 13 0 R] /DA (/F1 12 Tf 0 g) /DR << /Font << /F1 4 0 R >> >> >>",
        "<< /T (buyer) /Kids [7 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Jane Doe) /Parent 6 0 R /Rect [50 700 250 720] /P 3 0 R /F 4 >>",
        "<< /T (seller) /Kids [9 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (Acme Ltd) /Parent 8 0 R /Rect [50 650 250 670] /P 3 0 R /F 4 >>",
        "<< /FT /Btn /Ff 49152 /T (pay) /V /card /Kids [11 0 R 12 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /AS /card /AP << /N << /card 14 0 R /Off 14 0 R >> >> /Rect [50 600 62 612] /P 3 0 R /F 4 >>",
        "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /AS /Off /AP << /N << /cash 14 0 R /Off 14 0 R >> >> /Rect [80 600 92 612] /P 3 0 R /F 4 >>",
        "<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Yes /AS /Yes /AP << /N << /Yes 14 0 R /Off 14 0 R >> >> /Rect [50 550 62 562] /P 3 0 R /F 4 >>",
        StreamObject("<< /Type /XObject /Subtype /Form /BBox [0 0 12 12] ", ""));

    /// <summary>
    /// Dynamic XFA form: 1 catalog (NeedsRendering), 2 pages, 3 placeholder page, 4 AcroForm with an XFA packet array,
    /// 5 the datasets packet holding the filled value.
    /// </summary>
    public static byte[] DynamicXfaPdf(string value = "Jane Doe") => BuildPdf(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R /NeedsRendering true >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>",
        "<< /Fields [] /XFA [(datasets) 5 0 R] >>",
        StreamObject("<< ",
            $"<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><form><name>{value}</name></form></xfa:data></xfa:datasets>"));

    /// <summary>
    /// Text PDF carrying an embedded e-invoice: 1 catalog with an EmbeddedFiles name tree, 2 pages, 3 page, 4 content,
    /// 5 file specification, 6 Helvetica, 7 the embedded XML.
    /// </summary>
    public static byte[] PdfWithXmlAttachment(string name = "invoice.xml") => BuildPdf(
        $"<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [({name}) 5 0 R] >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 6 0 R >> >> >>",
        StreamObject("<< ", $"BT /F1 12 Tf 50 700 Td ({LongText}) Tj ET"),
        $"<< /Type /Filespec /F ({name}) /UF ({name}) /EF << /F 7 0 R >> >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        StreamObject("<< /Type /EmbeddedFile ", "<Invoice><Total>42.00</Total></Invoice>"));

    private static byte[] BuildPdf(params string[] objects)
    {
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
    }

    private static string StreamObject(string dictPrefix, string content) =>
        $"{dictPrefix}/Length {content.Length} >>\nstream\n{content}\nendstream";

    /// <summary>Multi-page CCITT G4 fax at standard resolution (204x98 dpi, so pixels are twice as tall as wide).</summary>
    public static byte[] FaxTiff(int pages = 2, uint width = 1728, uint height = 400)
    {
        using var collection = new MagickImageCollection();
        for (var p = 0; p < pages; p++)
        {
            var page = new MagickImage(MagickColors.White, width, height);
            var stripes = new Drawables().FillColor(MagickColors.Black);
            for (var y = 0; y < height; y += 40) stripes.Rectangle(0, y, width, y + 4);
            stripes.Draw(page);
            page.ColorType = ColorType.Bilevel;
            page.Density = new Density(204, 98, DensityUnit.PixelsPerInch);
            page.Settings.Compression = CompressionMethod.Group4;
            collection.Add(page);
        }
        return collection.ToByteArray(MagickFormat.Tiff);
    }

    public static byte[] Png(int width = 200, int height = 100)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.White);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A landscape JPEG tagged to display rotated 90° clockwise, as phone cameras store portrait photos.</summary>
    public static byte[] SidewaysJpeg(uint width = 200, uint height = 100)
    {
        // Magick writes the tag only from a non-empty EXIF profile, and overwrites it from Orientation on write.
        using var image = new MagickImage(MagickColors.White, width, height);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)OrientationType.RightTop);
        image.SetProfile(exif);
        image.Orientation = OrientationType.RightTop;
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>A PNG header that declares a huge size: a decompression bomb, as far as a decoder can tell.</summary>
    public static byte[] OversizedPngHeader(uint width = 100_000, uint height = 100_000)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // 8-bit greyscale
        PngChunk(ms, "IHDR", ihdr);
        PngChunk(ms, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        PngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void PngChunk(Stream stream, string type, byte[] data)
    {
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        stream.Write(header);
        stream.Write(typed);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, Crc32(typed));
        stream.Write(header);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>Page 1 has no text layer (rendered), page 2 is text (markdown).</summary>
    public static byte[] BlankThenTextPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        builder.AddPage(595, 842);
        builder.AddPage(595, 842).AddText(LongText, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        return builder.Build();
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

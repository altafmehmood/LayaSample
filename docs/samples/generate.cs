#:project ../../tests/LayaSample.Tests/LayaSample.Tests.csproj

// Regenerates the manual-testing samples in this folder: dotnet run docs/samples/generate.cs
// Most come from the test fixtures; the "-ocr" samples carry real text so OCR has something to read.

using ImageMagick;
using LayaSample.Tests;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

var dir = Path.GetDirectoryName(Path.GetFullPath(AppContext.GetData("EntryPointFilePath") as string ?? "docs/samples/generate.cs"))!;

string[] invoice =
[
    "INVOICE 2026-0417",
    "Supplier: Northwind Traders",
    "Customer: Contoso Ltd",
    "Widgets x 3        126.00",
    "Shipping            12.50",
    "Total due          138.50 EUR"
];

var samples = new Dictionary<string, byte[]>
{
    ["pdf/text.pdf"] = DocumentFixtures.TextPdf(),
    ["pdf/scanned-blank.pdf"] = DocumentFixtures.BlankPdf(),
    ["pdf/scanned-ocr.pdf"] = ImageOnlyPdf(TextImage(invoice)),
    ["pdf/mixed.pdf"] = DocumentFixtures.MixedPdf(),
    ["pdf/form-filled.pdf"] = DocumentFixtures.FilledFormPdf(),
    ["pdf/annotated-fill-and-sign.pdf"] = DocumentFixtures.AnnotatedPdf(),
    ["pdf/xfa-dynamic.pdf"] = DocumentFixtures.DynamicXfaPdf(),
    ["pdf/e-invoice-with-xml.pdf"] = DocumentFixtures.PdfWithXmlAttachment(),
    ["images/fax-blank.tif"] = DocumentFixtures.FaxTiff(),
    ["images/fax-ocr.tif"] = FaxTiff(TextImage(invoice), TextImage(["Page 2 of 2", "Payment terms: 30 days net"])),
    ["images/blank.png"] = DocumentFixtures.Png(),
    ["images/scan-ocr.png"] = Png(TextImage(invoice)),
    ["office/orders.xlsx"] = DocumentFixtures.Xlsx(),
    ["office/report.docx"] = DocumentFixtures.Docx(),
    ["unsupported/notes.txt"] = "Plain text is rejected with 415, whatever the file is called."u8.ToArray(),
    ["unsupported/legacy.doc"] = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0],
};

foreach (var (name, bytes) in samples)
{
    var path = Path.Combine(dir, name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, bytes);
    Console.WriteLine($"{name,-36} {bytes.Length,9:N0} bytes");
}

// A4 at 150 dpi, black text on white, like a scanned page.
static SKBitmap TextImage(string[] lines)
{
    var bitmap = new SKBitmap(1240, 1754);
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.White);
    using var font = new SKFont(SKTypeface.FromFamilyName("Helvetica"), 40);
    using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
    for (var i = 0; i < lines.Length; i++)
        canvas.DrawText(lines[i], 120, 200 + i * 70, font, paint);
    return bitmap;
}

static byte[] Png(SKBitmap bitmap)
{
    using (bitmap)
    using (var data = bitmap.Encode(SKEncodedImageFormat.Png, 100))
        return data.ToArray();
}

// A page whose only content is a full-page image: no text layer, as a scanner produces.
static byte[] ImageOnlyPdf(SKBitmap scan)
{
    var builder = new PdfDocumentBuilder();
    builder.AddPage(595, 842).AddPng(Png(scan), new PdfRectangle(0, 0, 595, 842));
    return builder.Build();
}

// CCITT G4, 204x98 dpi: vertical resolution is halved, as a standard-mode fax machine sends it.
static byte[] FaxTiff(params SKBitmap[] pages)
{
    using var collection = new MagickImageCollection();
    foreach (var page in pages)
    {
        var image = new MagickImage(Png(page));
        image.Resize(new MagickGeometry(1728, (uint)(image.Height * 1728 / image.Width * 98 / 204)) { IgnoreAspectRatio = true });
        image.ColorType = ColorType.Bilevel;
        image.Density = new Density(204, 98, DensityUnit.PixelsPerInch);
        image.Settings.Compression = CompressionMethod.Group4;
        collection.Add(image);
    }
    return collection.ToByteArray(MagickFormat.Tiff);
}

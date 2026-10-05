using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents;
using LayaSample.Rendering;
using Microsoft.Extensions.Options;

namespace LayaSample.Tests;

public class DocumentPreparerTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47];

    private static PreparedDocument Prepare(byte[] bytes, DocumentAnalysisOptions? options = null, FakeOcr? ocr = null)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(options ?? new DocumentAnalysisOptions());
        var rasterizer = new LocalPageRasterizer(ocr ?? new FakeOcr(), Microsoft.Extensions.Options.Options.Create(new RenderingOptions()));
        var classifier = new DocumentClassifier(opts, rasterizer);
        var preparer = new DocumentPreparer(opts, classifier, rasterizer, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentPreparer>.Instance);
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream(bytes);
        // The in-process rasterizer completes synchronously, so blocking here cannot deadlock.
        var classification = classifier.ClassifyAsync(stream, ct).GetAwaiter().GetResult();
        return preparer.PrepareAsync(stream, classification, ct).GetAwaiter().GetResult();
    }

    [Fact]
    public void Form_pdf_renders_pages_and_includes_field_values()
    {
        var prepared = Prepare(DocumentFixtures.FilledFormPdf());

        Assert.Collection(prepared.Parts,
            image =>
            {
                Assert.Equal((PartRole.PageImage, "image/png", 1), (image.Role, image.MediaType, image.Page));
                Assert.Equal(PngSignature, image.Data![..4]);
            },
            values =>
            {
                Assert.Equal((PartRole.StructuredData, DocumentSniffer.Json), (values.Role, values.MediaType));
                Assert.Contains("\"name\": \"Jane Doe\"", values.Text);
            });
    }

    [Fact]
    public void Form_values_are_keyed_by_fully_qualified_field_name()
    {
        var values = Assert.Single(Prepare(DocumentFixtures.NestedFormPdf()).Parts, p => p.Role == PartRole.StructuredData);

        using var json = System.Text.Json.JsonDocument.Parse(values.Text!);
        Assert.Equal(["buyer.name", "seller.name", "pay", "agree"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Jane Doe", json.RootElement.GetProperty("buyer.name").GetString());
        Assert.Equal("Acme Ltd", json.RootElement.GetProperty("seller.name").GetString());
        Assert.Equal("card", json.RootElement.GetProperty("pay").GetString());
        Assert.True(json.RootElement.GetProperty("agree").GetBoolean());
    }

    [Fact]
    public void Text_pdf_markdown_has_a_part_per_page()
    {
        var prepared = Prepare(DocumentFixtures.TextPdf());

        Assert.Equal(2, prepared.Parts.Count);
        Assert.All(prepared.Parts, p => Assert.Equal(PartRole.PageText, p.Role));
        Assert.Contains("## Page 1", prepared.Parts[0].Text);
        Assert.Contains("supplier delivers goods", prepared.Parts[0].Text);
    }

    [Fact]
    public void Scanned_pdf_hybrid_pairs_page_image_with_ocr_text()
    {
        var ocr = new FakeOcr("INVOICE 1234");
        var prepared = Prepare(DocumentFixtures.BlankPdf(), ocr: ocr);

        Assert.Equal(PreparationStrategy.Hybrid, prepared.Strategy);
        Assert.Collection(prepared.Parts,
            p => Assert.Equal((PartRole.PageImage, 1), (p.Role, p.Page)),
            p => Assert.Equal((PartRole.OcrText, 1, "INVOICE 1234"), (p.Role, p.Page, p.Text)));
        Assert.Single(ocr.Calls);
    }

    [Fact]
    public void Ocr_is_skipped_when_disabled()
    {
        var ocr = new FakeOcr();
        var prepared = Prepare(DocumentFixtures.BlankPdf(), new DocumentAnalysisOptions { EnableOcr = false }, ocr);

        Assert.Equal(PreparationStrategy.RenderToPng, prepared.Strategy);
        Assert.Equal(PartRole.PageImage, Assert.Single(prepared.Parts).Role);
        Assert.Empty(ocr.Calls);
    }

    [Fact]
    public void Mixed_pdf_is_prepared_per_page()
    {
        var prepared = Prepare(DocumentFixtures.MixedPdf());

        Assert.Equal(PreparationStrategy.PerPage, prepared.Strategy);
        Assert.Collection(prepared.Parts,
            p => Assert.Equal((PartRole.PageText, 1), (p.Role, p.Page)),
            p => Assert.Equal((PartRole.PageImage, 2), (p.Role, p.Page)),
            p => Assert.Equal((PartRole.OcrText, 2), (p.Role, p.Page)));
    }

    [Fact]
    public void Dynamic_xfa_returns_only_the_datasets()
    {
        var part = Assert.Single(Prepare(DocumentFixtures.DynamicXfaPdf("Jane Doe")).Parts);

        Assert.Equal((PartRole.StructuredData, DocumentSniffer.Xml), (part.Role, part.MediaType));
        Assert.Contains("<name>Jane Doe</name>", part.Text);
    }

    [Fact]
    public void Structured_data_without_any_data_is_unprocessable()
    {
        var ex = Assert.Throws<DocumentException>(() => Prepare(DocumentFixtures.DynamicXfaPdfWithoutData()));
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public void Embedded_xml_is_passed_through_with_its_source()
    {
        var prepared = Prepare(DocumentFixtures.PdfWithXmlAttachment("invoice.xml"));

        Assert.Equal(PartRole.PageText, prepared.Parts[0].Role);
        var xml = Assert.Single(prepared.Parts, p => p.Role == PartRole.StructuredData);
        Assert.Equal("invoice.xml", xml.Source);
        Assert.Contains("<Total>42.00</Total>", xml.Text);
    }

    [Fact]
    public void Fax_tiff_pages_are_converted_to_png_with_square_pixels_and_ocrd()
    {
        var ocr = new FakeOcr();
        var prepared = Prepare(DocumentFixtures.FaxTiff(pages: 2, width: 1728, height: 400), ocr: ocr);

        var images = prepared.Parts.Where(p => p.Role == PartRole.PageImage).ToList();
        Assert.Equal([1, 2], images.Select(p => p.Page));
        Assert.All(images, p => Assert.Equal(PngSignature, p.Data![..4]));
        Assert.Equal(2, prepared.Parts.Count(p => p.Role == PartRole.OcrText));
        // 204x98 dpi: height is stretched by 204/98 so the page is not squashed.
        Assert.All(ocr.Calls, c => Assert.Equal((1728, 833), c));
        // Fax pages are black and white, so they are returned as greyscale PNGs.
        Assert.All(images, p => Assert.Equal(SkiaSharp.SKColorType.Gray8, SkiaSharp.SKCodec.Create(new MemoryStream(p.Data!)).Info.ColorType));
    }

    [Fact]
    public void Image_pages_stop_at_max_pages()
    {
        var prepared = Prepare(DocumentFixtures.FaxTiff(pages: 3), new DocumentAnalysisOptions { MaxPages = 2 });
        Assert.Equal([1, 2], prepared.Parts.Where(p => p.Role == PartRole.PageImage).Select(p => p.Page));
    }

    [Fact]
    public void Jpeg_is_turned_upright_from_its_exif_orientation()
    {
        var ocr = new FakeOcr();
        Prepare(DocumentFixtures.SidewaysJpeg(width: 200, height: 100), ocr: ocr);
        Assert.Equal((100, 200), Assert.Single(ocr.Calls));
    }

    [Fact]
    public void Text_pages_after_the_render_cap_are_still_included()
    {
        var prepared = Prepare(DocumentFixtures.BlankThenTextPdf(), options: new DocumentAnalysisOptions { MaxPages = 0 });

        Assert.Equal(PreparationStrategy.PerPage, prepared.Strategy);
        Assert.Equal((PartRole.PageText, 2), (Assert.Single(prepared.Parts).Role, prepared.Parts[0].Page));
    }

    [Fact]
    public void Xlsx_markdown_contains_table()
    {
        var text = Assert.Single(Prepare(DocumentFixtures.Xlsx()).Parts).Text!;

        Assert.Contains("## Orders", text);
        Assert.Contains("| Item | Qty |", text);
        Assert.Contains("| Widget | 3 |", text);
    }

    [Fact]
    public void Annotated_pdf_renders_to_png()
    {
        var part = Assert.Single(Prepare(DocumentFixtures.AnnotatedPdf()).Parts);
        Assert.Equal(PngSignature, part.Data![..4]);
    }

    [Fact]
    public void Png_rendering_stops_at_max_pages()
    {
        var prepared = Prepare(DocumentFixtures.BlankPdf(pages: 2), new DocumentAnalysisOptions { MaxPages = 1 });
        Assert.Equal(1, Assert.Single(prepared.Parts, p => p.Role == PartRole.PageImage).Page);
    }

    [Fact]
    public void Xlsx_markdown_is_truncated_at_max_rows()
    {
        var text = Assert.Single(Prepare(DocumentFixtures.Xlsx(), options: new DocumentAnalysisOptions { MaxSpreadsheetRows = 1 }).Parts).Text!;

        Assert.Contains("| Item | Qty |", text);
        Assert.DoesNotContain("Widget", text);
        Assert.Contains("showing 1 of 2 rows", text);
    }

    [Fact]
    public void Docx_markdown_contains_paragraphs()
    {
        var text = Assert.Single(Prepare(DocumentFixtures.Docx()).Parts).Text!;

        Assert.Contains("Quarterly report", text);
        Assert.Contains("Revenue grew this quarter.", text);
    }

    [Fact]
    public void Render_budget_is_shared_with_embedded_documents()
    {
        // Carrier page without a text layer (rendered), plus an embedded scanned PDF (also rendered).
        var pdf = DocumentFixtures.PdfWithAttachment("scan.pdf", DocumentFixtures.BlankPdf(), pageContent: "");

        var all = Prepare(pdf);
        var capped = Prepare(pdf, options: new DocumentAnalysisOptions { MaxPages = 1 });

        Assert.Equal(2, all.Parts.Count(p => p.Role == PartRole.PageImage));
        var image = Assert.Single(capped.Parts, p => p.Role == PartRole.PageImage);
        Assert.Null(image.Source);
    }

    [Fact]
    public void Xml_attachment_is_decoded_with_its_declared_encoding()
    {
        var xml = System.Text.Encoding.Latin1.GetBytes("<?xml version=\"1.0\" encoding=\"ISO-8859-1\"?><Invoice><Name>Müller</Name></Invoice>");

        var part = Assert.Single(Prepare(DocumentFixtures.PdfWithAttachment("invoice.xml", xml)).Parts, p => p.Source == "invoice.xml");

        Assert.Contains("<Name>Müller</Name>", part.Text);
    }

    [Fact]
    public void Docx_content_controls_and_lists_are_converted()
    {
        var text = Assert.Single(Prepare(DocumentFixtures.DocxWithContentControlAndList()).Parts).Text!;

        Assert.Contains("Customer: Jane Doe", text);
        Assert.Contains("- First item", text);
    }
}

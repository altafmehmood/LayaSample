using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents;
using LayaSample.Rendering;
using Microsoft.Extensions.Options;

namespace LayaSample.Tests;

public class DocumentClassifierTests
{
    private static readonly IOptions<DocumentAnalysisOptions> Options = Microsoft.Extensions.Options.Options.Create(new DocumentAnalysisOptions());

    internal static DocumentClassifier Classifier(IOptions<DocumentAnalysisOptions> options) =>
        new(options, new LocalPageRasterizer(new FakeOcr(), Microsoft.Extensions.Options.Options.Create(new RenderingOptions())));

    // The in-process rasterizer completes synchronously, so blocking here cannot deadlock.
    private static DocumentClassification Classify(byte[] bytes, DocumentAnalysisOptions? options = null) =>
        Classifier(options is null ? Options : Microsoft.Extensions.Options.Options.Create(options))
            .ClassifyAsync(new MemoryStream(bytes), TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    [Fact]
    public void Filled_form_pdf_is_rendered_to_png()
    {
        var result = Classify(DocumentFixtures.FilledFormPdf());

        Assert.Equal(DocumentKind.FormPdf, result.Kind);
        Assert.Equal(PreparationStrategy.RenderToPng, result.Strategy);
        Assert.Equal(DocumentRoutes.Form, result.Route);
        Assert.Equal(1, result.FormFieldCount);
        Assert.Equal(1, result.FilledFormFieldCount);
    }

    [Fact]
    public void Radio_and_checkbox_groups_count_as_one_field_each()
    {
        var result = Classify(DocumentFixtures.NestedFormPdf());

        // buyer.name, seller.name, pay (a radio group with two buttons), agree.
        Assert.Equal(DocumentKind.FormPdf, result.Kind);
        Assert.Equal(4, result.FormFieldCount);
        Assert.Equal(4, result.FilledFormFieldCount);
    }

    [Fact]
    public void Pdf_filled_with_annotations_is_rendered_to_png()
    {
        var result = Classify(DocumentFixtures.AnnotatedPdf());

        Assert.Equal(DocumentKind.AnnotatedPdf, result.Kind);
        Assert.Equal(PreparationStrategy.RenderToPng, result.Strategy);
        Assert.Equal(DocumentRoutes.Form, result.Route);
        Assert.Equal(1, result.MarkupAnnotationCount);
    }

    [Fact]
    public void Text_pdf_is_converted_to_markdown()
    {
        var result = Classify(DocumentFixtures.TextPdf());

        Assert.Equal(DocumentKind.TextPdf, result.Kind);
        Assert.Equal(PreparationStrategy.Markdown, result.Strategy);
        Assert.Equal(DocumentRoutes.Text, result.Route);
        Assert.Equal(2, result.PageCount);
    }

    [Fact]
    public void Pdf_without_text_layer_is_treated_as_scanned()
    {
        var result = Classify(DocumentFixtures.BlankPdf());

        Assert.Equal(DocumentKind.ScannedPdf, result.Kind);
        Assert.Equal(PreparationStrategy.Hybrid, result.Strategy);
        Assert.Equal(DocumentRoutes.Vision, result.Route);
    }

    [Fact]
    public void Scanned_pdf_is_only_rendered_when_ocr_is_disabled()
    {
        var result = Classify(DocumentFixtures.BlankPdf(), new DocumentAnalysisOptions { EnableOcr = false });

        Assert.Equal(PreparationStrategy.RenderToPng, result.Strategy);
    }

    [Fact]
    public void Pdf_with_some_text_free_pages_is_mixed_and_prepared_per_page()
    {
        var result = Classify(DocumentFixtures.MixedPdf());

        Assert.Equal(DocumentKind.MixedPdf, result.Kind);
        Assert.Equal(PreparationStrategy.PerPage, result.Strategy);
        Assert.Collection(result.Pages,
            p => Assert.Equal((PageContent.Text, PreparationStrategy.Markdown), (p.Content, p.Strategy)),
            p => Assert.Equal((PageContent.Scanned, PreparationStrategy.Hybrid), (p.Content, p.Strategy)));
    }

    [Fact]
    public void Dynamic_xfa_pdf_uses_structured_data()
    {
        var result = Classify(DocumentFixtures.DynamicXfaPdf());

        Assert.Equal(DocumentKind.XfaPdf, result.Kind);
        Assert.Equal(PreparationStrategy.StructuredData, result.Strategy);
        Assert.Equal(DocumentRoutes.Form, result.Route);
        Assert.True(result.IsDynamicXfa);
        Assert.Contains("form data extracted", result.Reason);
    }

    [Fact]
    public void Embedded_files_are_listed()
    {
        var result = Classify(DocumentFixtures.PdfWithXmlAttachment());

        Assert.Equal(DocumentKind.TextPdf, result.Kind);
        var attachment = Assert.Single(result.Attachments);
        Assert.Equal("invoice.xml", attachment.Name);
        Assert.Equal(DocumentSniffer.Xml, attachment.MediaType);
    }

    [Fact]
    public void Multi_page_fax_tiff_is_an_image_with_fax_pages()
    {
        var result = Classify(DocumentFixtures.FaxTiff(pages: 3));

        Assert.Equal(DocumentKind.Image, result.Kind);
        Assert.Equal(DocumentSniffer.Tiff, result.MediaType);
        Assert.Equal(PreparationStrategy.Hybrid, result.Strategy);
        Assert.Equal(DocumentRoutes.Vision, result.Route);
        Assert.Equal(3, result.PageCount);
        Assert.All(result.Pages, p => Assert.Equal(PageContent.Fax, p.Content));
    }

    [Fact]
    public void Png_is_a_single_scanned_page()
    {
        var result = Classify(DocumentFixtures.Png());

        Assert.Equal(DocumentKind.Image, result.Kind);
        Assert.Equal(PageContent.Scanned, Assert.Single(result.Pages).Content);
    }

    [Fact]
    public void Corrupt_image_is_unprocessable()
    {
        byte[] truncatedPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0];
        var ex = Assert.Throws<DocumentException>(() => Classify(truncatedPng));
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public void Image_declaring_a_huge_size_is_rejected_before_decoding()
    {
        var ex = Assert.Throws<DocumentException>(() => Classify(DocumentFixtures.OversizedPngHeader()));
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("out of range", ex.InnerException?.Message);
    }

    [Fact]
    public void Xlsx_is_converted_to_markdown()
    {
        var result = Classify(DocumentFixtures.Xlsx());

        Assert.Equal(DocumentKind.Spreadsheet, result.Kind);
        Assert.Equal(PreparationStrategy.Markdown, result.Strategy);
        Assert.Equal(DocumentRoutes.Spreadsheet, result.Route);
    }

    [Fact]
    public void Docx_is_converted_to_markdown()
    {
        var result = Classify(DocumentFixtures.Docx());

        Assert.Equal(DocumentKind.WordDocument, result.Kind);
        Assert.Equal(PreparationStrategy.Markdown, result.Strategy);
        Assert.Equal(DocumentRoutes.Text, result.Route);
    }

    [Fact]
    public void Plain_text_is_unsupported_even_if_named_pdf()
    {
        var ex = Assert.Throws<DocumentException>(() => Classify("just some text"u8.ToArray()));
        Assert.Equal(415, ex.StatusCode);
    }

    [Fact]
    public void Legacy_or_encrypted_office_file_is_unsupported()
    {
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
        var ex = Assert.Throws<DocumentException>(() => Classify(ole));
        Assert.Equal(415, ex.StatusCode);
        Assert.Contains("password-protected", ex.Message);
    }

    [Fact]
    public void Zip_that_is_not_office_is_unsupported()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            zip.CreateEntry("readme.txt");
        var ex = Assert.Throws<DocumentException>(() => Classify(ms.ToArray()));
        Assert.Equal(415, ex.StatusCode);
    }

    [Fact]
    public void Truncated_pdf_is_unprocessable()
    {
        var ex = Assert.Throws<DocumentException>(() => Classify("%PDF-1.7\n1 0 obj"u8.ToArray()));
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public void Valid_signature_covering_the_file_is_verified()
    {
        var signature = Assert.Single(Classify(DocumentFixtures.SignedPdf()).Signatures);

        Assert.Equal(("Signature1", "Test Signer", true, true, null),
            (signature.FieldName, signature.Signer, signature.IntegrityValid, signature.CoversWholeDocument, signature.Error));
        Assert.NotNull(signature.SigningTime);
    }

    [Fact]
    public void Tampered_signed_content_fails_integrity()
    {
        var signature = Assert.Single(Classify(DocumentFixtures.SignedPdf(DocumentFixtures.SignatureCase.Tampered)).Signatures);

        Assert.False(signature.IntegrityValid);
        Assert.NotNull(signature.Error);
    }

    [Fact]
    public void Content_appended_after_signing_is_reported()
    {
        var signature = Assert.Single(Classify(DocumentFixtures.SignedPdf(DocumentFixtures.SignatureCase.UpdatedAfterSigning)).Signatures);

        Assert.True(signature.IntegrityValid);
        Assert.False(signature.CoversWholeDocument);
        Assert.Contains("incremental update", signature.Error);
    }

    [Fact]
    public void Office_file_that_unpacks_too_large_is_rejected()
    {
        var bomb = DocumentFixtures.XlsxWithPadding(2 * 1024 * 1024);

        var ex = Assert.Throws<DocumentException>(() => Classify(bomb, new DocumentAnalysisOptions { MaxOfficeUncompressedBytes = 1024 * 1024 }));
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("unpacked", ex.Message);
    }

    [Fact]
    public void Pdf_with_too_many_pages_is_rejected()
    {
        var ex = Assert.Throws<DocumentException>(() => Classify(DocumentFixtures.TextPdf(), new DocumentAnalysisOptions { MaxPdfPages = 1 }));
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("2 pages", ex.Message);
    }

    [Fact]
    public void Tiff_with_too_many_pages_is_rejected()
    {
        var ex = Assert.Throws<DocumentException>(() => Classify(DocumentFixtures.FaxTiff(pages: 3), new DocumentAnalysisOptions { MaxImageFrames = 2 }));
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public void Image_whose_metadata_mentions_pdf_is_still_an_image()
    {
        var result = Classify(DocumentFixtures.PngMentioningPdf());

        Assert.Equal((DocumentKind.Image, "image/png"), (result.Kind, result.MediaType));
    }

    [Fact]
    public async Task Classification_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Classifier(Options).ClassifyAsync(new MemoryStream(DocumentFixtures.TextPdf()), cts.Token));
    }
}

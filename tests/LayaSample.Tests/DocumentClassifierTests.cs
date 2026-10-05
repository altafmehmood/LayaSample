using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents;
using Microsoft.Extensions.Options;

namespace LayaSample.Tests;

public class DocumentClassifierTests
{
    private static readonly IOptions<DocumentAnalysisOptions> Options = Microsoft.Extensions.Options.Options.Create(new DocumentAnalysisOptions());

    private static DocumentClassification Classify(byte[] bytes) =>
        new DocumentClassifier(Options).Classify(new MemoryStream(bytes));

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
        Assert.Equal(PreparationStrategy.RenderToPng, result.Strategy);
        Assert.Equal(DocumentRoutes.Vision, result.Route);
    }

    [Fact]
    public void Pdf_with_some_text_free_pages_is_mixed()
    {
        var result = Classify(DocumentFixtures.MixedPdf());

        Assert.Equal(DocumentKind.MixedPdf, result.Kind);
        Assert.Equal(PreparationStrategy.RenderToPng, result.Strategy);
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
}

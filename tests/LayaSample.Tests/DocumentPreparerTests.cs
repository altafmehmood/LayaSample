using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents;
using Microsoft.Extensions.Options;

namespace LayaSample.Tests;

public class DocumentPreparerTests
{
    private static readonly IOptions<DocumentAnalysisOptions> Options = Microsoft.Extensions.Options.Options.Create(new DocumentAnalysisOptions());
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47];

    private static PreparedDocument Prepare(byte[] bytes, PreparationStrategy? strategy = null)
    {
        using var stream = new MemoryStream(bytes);
        var classification = new DocumentClassifier(Options).Classify(stream);
        return new DocumentPreparer(Options).Prepare(stream, classification, strategy ?? classification.Strategy);
    }

    [Fact]
    public void Form_pdf_renders_one_png_per_page()
    {
        var prepared = Prepare(DocumentFixtures.FilledFormPdf());

        var part = Assert.Single(prepared.Parts);
        Assert.Equal("image/png", part.MediaType);
        Assert.Equal(1, part.Page);
        Assert.Equal(PngSignature, part.Data![..4]);
    }

    [Fact]
    public void Text_pdf_markdown_has_a_part_per_page()
    {
        var prepared = Prepare(DocumentFixtures.TextPdf());

        Assert.Equal(2, prepared.Parts.Count);
        Assert.Contains("## Page 1", prepared.Parts[0].Text);
        Assert.Contains("supplier delivers goods", prepared.Parts[0].Text);
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
    public void Docx_markdown_contains_paragraphs()
    {
        var text = Assert.Single(Prepare(DocumentFixtures.Docx()).Parts).Text!;

        Assert.Contains("Quarterly report", text);
        Assert.Contains("Revenue grew this quarter.", text);
    }

    [Fact]
    public void As_is_returns_original_bytes()
    {
        var bytes = DocumentFixtures.TextPdf();

        var part = Assert.Single(Prepare(bytes, PreparationStrategy.AsIs).Parts);

        Assert.Equal("application/pdf", part.MediaType);
        Assert.Equal(bytes, part.Data);
    }

    [Fact]
    public void Png_strategy_is_rejected_for_non_pdf()
    {
        var ex = Assert.Throws<DocumentException>(() => Prepare(DocumentFixtures.Xlsx(), PreparationStrategy.RenderToPng));
        Assert.Equal(400, ex.StatusCode);
    }
}

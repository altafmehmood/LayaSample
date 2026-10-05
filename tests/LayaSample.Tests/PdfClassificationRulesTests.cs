using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents;
using LayaSample.Api.Services.Documents.Pdf;

namespace LayaSample.Tests;

/// <summary>Signal combinations that are hard to build as real PDFs (broken encodings, fax scans, OCR layers).</summary>
public class PdfClassificationRulesTests
{
    private static readonly DocumentAnalysisOptions Options = new();

    private static PageSignals TextPage(int number = 1, int chars = 500) =>
        new(number) { Chars = chars, NonSpaceChars = chars, AlphanumericChars = chars };

    private static DocumentClassification Decide(params PageSignals[] pages) => Decide(new DocumentSignals(), pages);

    private static DocumentClassification Decide(DocumentSignals doc, params PageSignals[] pages) =>
        PdfClassificationRules.Decide(doc, pages, Options);

    [Fact]
    public void Garbage_text_layer_is_broken_and_ocrd()
    {
        var page = TextPage();
        page.BadChars = 300;

        var result = Decide(page);

        Assert.Equal(DocumentKind.BrokenTextPdf, result.Kind);
        Assert.Equal(PreparationStrategy.Hybrid, result.Strategy);
        Assert.Equal(DocumentRoutes.Vision, result.Route);
    }

    [Fact]
    public void Symbol_soup_text_layer_is_broken()
    {
        var page = TextPage();
        page.AlphanumericChars = 100;

        Assert.Equal(PageContent.BrokenText, Assert.Single(Decide(page).Pages).Content);
    }

    [Fact]
    public void Fax_page_renders_at_native_resolution_capped_by_max_dpi()
    {
        var fine = new PageSignals(1) { HasFaxImage = true, HasFullPageImage = true, NativeImageDpi = 204 };
        var huge = new PageSignals(2) { HasFaxImage = true, HasFullPageImage = true, NativeImageDpi = 600 };

        var result = Decide(fine, huge);

        Assert.Equal(DocumentKind.FaxPdf, result.Kind);
        Assert.Equal(204, result.Pages[0].RenderDpi);
        Assert.Equal(Options.MaxRenderDpi, result.Pages[1].RenderDpi);
    }

    [Fact]
    public void Text_over_full_page_image_is_a_scan_with_ocr_layer()
    {
        var page = TextPage();
        page.HasFullPageImage = true;

        var result = Decide(page);

        Assert.Equal(DocumentKind.ScannedPdf, result.Kind);
        Assert.Equal(PageContent.OcrLayer, result.Pages[0].Content);
        Assert.Equal(PreparationStrategy.Hybrid, result.Strategy);
    }

    [Fact]
    public void Path_heavy_page_is_layout()
    {
        var page = TextPage();
        page.Paths = Options.LayoutMinPaths;

        Assert.Equal(DocumentKind.LayoutPdf, Decide(page).Kind);
    }

    [Fact]
    public void Form_takes_precedence_and_only_widget_pages_render()
    {
        var formPage = TextPage(1);
        formPage.Widgets = 2;
        var doc = new DocumentSignals { FormFields = 2, FilledFormFields = 1 };

        var result = Decide(doc, formPage, TextPage(2));

        Assert.Equal(DocumentKind.FormPdf, result.Kind);
        Assert.Equal(PreparationStrategy.PerPage, result.Strategy);
        Assert.Equal(PreparationStrategy.RenderToPng, result.Pages[0].Strategy);
        Assert.Equal(PreparationStrategy.Markdown, result.Pages[1].Strategy);
    }

    [Fact]
    public void Signatures_and_attachments_are_mentioned_in_the_reason()
    {
        var doc = new DocumentSignals();
        doc.Signatures.Add(new SignatureInfo("sig", "Jane", null, true, true, null));
        doc.Attachments.Add(new AttachmentInfo("invoice.xml", 10, DocumentSniffer.Xml));

        var result = Decide(doc, TextPage());

        Assert.Contains("1 attachment(s)", result.Reason);
        Assert.Contains("1 signature(s)", result.Reason);
        Assert.Single(result.Signatures);
    }
}

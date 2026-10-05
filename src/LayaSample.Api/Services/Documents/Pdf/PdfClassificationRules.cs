using LayaSample.Api.Models;

namespace LayaSample.Api.Services.Documents.Pdf;

/// <summary>Turns detector signals into per-page and document-level decisions. Pure, so it is unit-testable without PDFs.</summary>
public static class PdfClassificationRules
{
    public static DocumentClassification Decide(DocumentSignals doc, IReadOnlyList<PageSignals> pages, DocumentAnalysisOptions options)
    {
        var classified = pages.Select(p => ClassifyPage(p, doc, options)).ToList();
        var (kind, route, reason) = DecideKind(doc, classified);

        var strategies = classified.Select(p => p.Strategy).Distinct().ToList();
        var strategy = doc.IsDynamicXfa ? PreparationStrategy.StructuredData : strategies.Count switch
        {
            0 => PreparationStrategy.Markdown,
            1 => strategies[0],
            _ => PreparationStrategy.PerPage
        };

        var chars = pages.Sum(p => (long)p.Chars);
        return new DocumentClassification
        {
            Kind = kind,
            Strategy = strategy,
            Route = route,
            Reason = reason,
            MediaType = DocumentSniffer.Pdf,
            PageCount = pages.Count,
            FormFieldCount = doc.FormFields,
            FilledFormFieldCount = doc.FilledFormFields,
            MarkupAnnotationCount = pages.Sum(p => p.MarkupAnnotations),
            ImageCount = pages.Sum(p => p.Images),
            AvgCharsPerPage = pages.Count == 0 ? 0 : Math.Round((double)chars / pages.Count, 1),
            IsXfa = doc.IsXfa,
            IsDynamicXfa = doc.IsDynamicXfa,
            Pages = classified,
            Signatures = doc.Signatures,
            Attachments = doc.Attachments
        };
    }

    public static PageClassification ClassifyPage(PageSignals p, DocumentSignals doc, DocumentAnalysisOptions o)
    {
        var content = Content(p, doc, o);
        var strategy = content switch
        {
            PageContent.Form or PageContent.Annotated => PreparationStrategy.RenderToPng,
            PageContent.Scanned or PageContent.Fax or PageContent.BrokenText => o.EnableOcr ? PreparationStrategy.Hybrid : PreparationStrategy.RenderToPng,
            PageContent.OcrLayer or PageContent.Layout => PreparationStrategy.Hybrid,
            PageContent.XfaPlaceholder => PreparationStrategy.StructuredData,
            _ => PreparationStrategy.Markdown
        };
        int? dpi = content == PageContent.Fax && p.NativeImageDpi > o.PngDpi ? Math.Min(p.NativeImageDpi.Value, o.MaxRenderDpi) : null;
        return new PageClassification(p.Number, content, strategy, p.Chars, p.Images, Math.Round(p.GarbageRatio, 3), dpi);
    }

    private static PageContent Content(PageSignals p, DocumentSignals doc, DocumentAnalysisOptions o)
    {
        if (doc.IsDynamicXfa) return PageContent.XfaPlaceholder;
        // Field values and Fill & Sign content are outside the text layer: only a render shows them.
        if (p.Widgets > 0 && doc.FormFields > 0) return PageContent.Form;
        if (p.MarkupAnnotations > 0) return PageContent.Annotated;
        if (p.Chars < o.MinCharsPerPage) return p.HasFaxImage ? PageContent.Fax : PageContent.Scanned;
        if (p.GarbageRatio > o.MaxGarbageRatio || p.AlphanumericRatio < o.MinAlphanumericRatio) return PageContent.BrokenText;
        // Text over a full-page image is a scan with an OCR layer: the text may be poor, so keep the image too.
        if (p.HasFullPageImage) return PageContent.OcrLayer;
        if (p.Paths >= o.LayoutMinPaths) return PageContent.Layout;
        return PageContent.Text;
    }

    private static (DocumentKind Kind, string Route, string Reason) DecideKind(DocumentSignals doc, IReadOnlyList<PageClassification> pages)
    {
        var summary = string.Join(", ", pages.GroupBy(p => p.Content).Select(g => $"{g.Count()} {g.Key} page(s)"));
        var extras = (doc.Attachments.Count > 0 ? $"; {doc.Attachments.Count} attachment(s)" : "")
                   + (doc.Signatures.Count > 0 ? $"; {doc.Signatures.Count} signature(s)" : "");

        if (doc.IsDynamicXfa)
            return (DocumentKind.XfaPdf, DocumentRoutes.Form, doc.HasXfaData
                ? "dynamic XFA form; pages are a viewer placeholder, form data extracted from XFA datasets" + extras
                : "dynamic XFA form; pages are a viewer placeholder and no XFA datasets were found" + extras);
        if (doc.FormFields > 0)
            return (DocumentKind.FormPdf, DocumentRoutes.Form,
                $"{doc.FormFields} form field(s), {doc.FilledFormFields} filled; form pages rendered so values are visible ({summary}){extras}");
        if (pages.Any(p => p.Content == PageContent.Annotated))
            return (DocumentKind.AnnotatedPdf, DocumentRoutes.Form,
                $"text/ink/stamp annotations outside the text layer; annotated pages rendered ({summary}){extras}");

        var contents = pages.Select(p => p.Content).ToHashSet();
        var kind = contents switch
        {
            { Count: 0 } => DocumentKind.TextPdf,
            _ when contents.All(c => c == PageContent.Fax) => DocumentKind.FaxPdf,
            _ when contents.All(c => c is PageContent.Scanned or PageContent.Fax or PageContent.OcrLayer) => DocumentKind.ScannedPdf,
            _ when contents.All(c => c == PageContent.BrokenText) => DocumentKind.BrokenTextPdf,
            _ when contents.All(c => c == PageContent.Layout) => DocumentKind.LayoutPdf,
            _ when contents.All(c => c == PageContent.Text) => DocumentKind.TextPdf,
            _ => DocumentKind.MixedPdf
        };
        return (kind, kind == DocumentKind.TextPdf ? DocumentRoutes.Text : DocumentRoutes.Vision, (summary.Length == 0 ? "no pages" : summary) + extras);
    }
}

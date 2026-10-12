using DocIntake.Contracts;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;

namespace DocIntake.Api.Services.Documents.Pdf;

/// <summary>
/// PDF actions that run code or a program, and embedded rich media. The service never runs any of it: PdfPig and PDFium
/// do not execute scripts. These are reported because a file that carries them is not an ordinary lending document, and
/// because the people who receive the extracted data or the original file may open it in a viewer that does.
/// JavaScript alone is only reported (fillable forms use it to format fields); Launch and rich media block.
/// </summary>
public sealed class ActiveContentDetector : IPdfDetector
{
    private const int MaxActionChain = 8;

    public void InspectDocument(PdfInspection inspection)
    {
        var pdf = inspection.Pdf;
        var catalog = PdfTokens.Catalog(pdf);
        CheckAction(inspection, catalog.Data.GetValueOrDefault("OpenAction"), null, 0);
        CheckAdditionalActions(inspection, catalog.Data.GetValueOrDefault("AA"), null);

        var names = PdfTokens.Get<DictionaryToken>(pdf, catalog, "Names");
        if (names is not null && names.Data.ContainsKey("JavaScript"))
            inspection.Document.AddRisk(RiskKinds.ActiveContent, RiskSeverity.Info, null, "document-level JavaScript");
    }

    public void InspectPage(PdfInspection inspection, Page page, PageSignals signals)
    {
        var pdf = inspection.Pdf;
        // Page-level additional actions (/AA on the page itself) are not reachable through PdfPig's public API; the
        // document's OpenAction and /AA, and the actions on each annotation and form widget, are inspected.
        foreach (var annotation in page.GetAnnotations())
        {
            var dictionary = annotation.AnnotationDictionary;
            var subtype = PdfTokens.Get<NameToken>(pdf, dictionary, "Subtype")?.Data;
            if (subtype is "RichMedia" or "3D")
                inspection.Document.AddRisk(RiskKinds.ActiveContent, RiskSeverity.Block, page.Number, $"embedded {subtype} content (Flash, video or 3D)");
            CheckAction(inspection, dictionary.Data.GetValueOrDefault("A"), page.Number, 0);
            CheckAdditionalActions(inspection, dictionary.Data.GetValueOrDefault("AA"), page.Number);
        }
    }

    private static void CheckAdditionalActions(PdfInspection inspection, IToken? token, int? page)
    {
        if (PdfTokens.Resolve(inspection.Pdf, token) is not DictionaryToken actions) return;
        foreach (var action in actions.Data.Values)
            CheckAction(inspection, action, page, 0);
    }

    private static void CheckAction(PdfInspection inspection, IToken? token, int? page, int depth)
    {
        if (depth > MaxActionChain) return;
        switch (PdfTokens.Resolve(inspection.Pdf, token))
        {
            case ArrayToken chain:
                foreach (var item in chain.Data)
                    CheckAction(inspection, item, page, depth + 1);
                break;
            case DictionaryToken action:
                var type = PdfTokens.Get<NameToken>(inspection.Pdf, action, "S")?.Data;
                if (type == "Launch")
                    inspection.Document.AddRisk(RiskKinds.ActiveContent, RiskSeverity.Block, page, "a Launch action (runs a program or opens a file)");
                else if (type == "JavaScript" || action.Data.ContainsKey("JS"))
                    inspection.Document.AddRisk(RiskKinds.ActiveContent, RiskSeverity.Info, page, "a JavaScript action");
                else if (type is "SubmitForm" or "ImportData")
                    inspection.Document.AddRisk(RiskKinds.ActiveContent, RiskSeverity.Info, page, $"a {type} action");
                CheckAction(inspection, action.Data.GetValueOrDefault("Next"), page, depth + 1);
                break;
        }
    }
}

/// <summary>
/// Text a person reading the page cannot see: tiny, near white, or outside the page. It is the usual way to hide an
/// instruction for a model in a document. Pages that are a scan with a text layer over the image are left to the
/// layer-mismatch check, because that layer is invisible by design.
/// </summary>
public sealed class HiddenTextDetector : IPdfDetector
{
    /// <summary>Enough characters to carry an instruction; a few stray ones are layout.</summary>
    public const int MinCharacters = 20;
    private const double MinPointSize = 1.5;
    private const double NearWhite = 0.96;
    private const double OffPageMargin = 2;

    public void InspectPage(PdfInspection inspection, Page page, PageSignals signals)
    {
        if (signals.HasFullPageImage) return;

        int tiny = 0, white = 0, outside = 0;
        var box = page.CropBox.Bounds;
        foreach (var letter in page.Letters)
        {
            if (letter.Value.Length == 0 || char.IsWhiteSpace(letter.Value[0])) continue;
            if (letter.PointSize > 0 && letter.PointSize < MinPointSize) tiny++;
            else if (IsNearWhite(letter.Color)) white++;
            var r = letter.BoundingBox;
            if (r.Right < box.Left - OffPageMargin || r.Left > box.Right + OffPageMargin
                || r.Top < box.Bottom - OffPageMargin || r.Bottom > box.Top + OffPageMargin)
                outside++;
        }

        Report(inspection, page.Number, tiny, "tiny text (under 1.5 pt)");
        Report(inspection, page.Number, white, "near-white text");
        Report(inspection, page.Number, outside, "text outside the page");
    }

    private static void Report(PdfInspection inspection, int page, int characters, string what)
    {
        if (characters >= MinCharacters)
            inspection.Document.AddRisk(RiskKinds.HiddenText, RiskSeverity.Block, page, $"{characters} characters of {what}");
    }

    private static bool IsNearWhite(UglyToad.PdfPig.Graphics.Colors.IColor? color)
    {
        if (color is null) return false;
        var (r, g, b) = color.ToRGBValues();
        return (double)r >= NearWhite && (double)g >= NearWhite && (double)b >= NearWhite;
    }
}

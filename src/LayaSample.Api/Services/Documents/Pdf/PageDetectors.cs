using System.Globalization;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;

namespace LayaSample.Api.Services.Documents.Pdf;

/// <summary>Measures the text layer and how much of it decodes to real characters.</summary>
public sealed class TextLayerDetector : IPdfDetector
{
    public void InspectPage(PdfInspection inspection, Page page, PageSignals signals)
    {
        foreach (var letter in page.Letters)
        {
            foreach (var c in letter.Value)
            {
                signals.Chars++;
                if (char.IsWhiteSpace(c)) continue;
                signals.NonSpaceChars++;
                if (char.IsLetterOrDigit(c)) signals.AlphanumericChars++;
                if (IsUndecodable(c)) signals.BadChars++;
            }
        }
    }

    /// <summary>Characters a correct ToUnicode mapping never produces in body text.</summary>
    public static bool IsUndecodable(char c) => c == '�' || char.GetUnicodeCategory(c) switch
    {
        UnicodeCategory.Control or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate => true,
        _ => false
    };
}

/// <summary>Counts images and spots fax encodings and full-page scans.</summary>
public sealed class ImageDetector : IPdfDetector
{
    // Inline images use the abbreviated filter names.
    private static readonly HashSet<string> FaxFilters = ["CCITTFaxDecode", "CCF", "JBIG2Decode"];

    public void InspectPage(PdfInspection inspection, Page page, PageSignals signals)
    {
        var pageArea = page.Width * page.Height;
        foreach (var image in page.GetImages())
        {
            signals.Images++;
            var bounds = image.BoundingBox;
            var coversPage = pageArea > 0 && Math.Abs(bounds.Width * bounds.Height) / pageArea >= inspection.Options.FullPageImageCoverage;
            if (coversPage) signals.HasFullPageImage = true;

            var isFax = Filters(image).Any(FaxFilters.Contains) || (coversPage && image.BitsPerComponent == 1);
            if (isFax) signals.HasFaxImage = true;

            if (coversPage && bounds.Width > 0)
            {
                // Rendering at the image's own resolution keeps fax / scan detail; page geometry fixes the aspect ratio.
                var dpi = (int)Math.Round(image.WidthInSamples / (Math.Abs(bounds.Width) / 72.0));
                signals.NativeImageDpi = Math.Max(signals.NativeImageDpi ?? 0, dpi);
            }
        }
    }

    private static IEnumerable<string> Filters(IPdfImage image)
    {
        var filter = image.ImageDictionary.Data.GetValueOrDefault("Filter") ?? image.ImageDictionary.Data.GetValueOrDefault("F");
        return filter switch
        {
            NameToken name => [name.Data],
            ArrayToken array => array.Data.OfType<NameToken>().Select(n => n.Data),
            _ => []
        };
    }
}

/// <summary>Counts vector paths; tables and drawings produce many.</summary>
public sealed class LayoutDetector : IPdfDetector
{
    public void InspectPage(PdfInspection inspection, Page page, PageSignals signals) => signals.Paths = page.Paths.Count;
}

/// <summary>Finds form widgets and annotations that put visible content outside the text layer (Fill &amp; Sign).</summary>
public sealed class AnnotationDetector : IPdfDetector
{
    private static readonly HashSet<AnnotationType> ContentAnnotations = [AnnotationType.FreeText, AnnotationType.Ink, AnnotationType.Stamp];

    public void InspectPage(PdfInspection inspection, Page page, PageSignals signals)
    {
        foreach (var annotation in page.GetAnnotations())
        {
            if (annotation.Type == AnnotationType.Widget) signals.Widgets++;
            else if (ContentAnnotations.Contains(annotation.Type)) signals.MarkupAnnotations++;
        }
    }
}

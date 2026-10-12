using System.ComponentModel.DataAnnotations;

namespace DocIntake.DocumentService;

/// <summary>
/// Settings for classification and preparation, which run in the document service. Rendering and OCR settings (OcrModel,
/// OcrMaxSide) are in <see cref="DocIntake.Rendering.RenderingOptions"/>, bound from the same section. The gateway's own
/// settings (file size, timeouts, concurrency, where this service is) are in <c>DocumentGatewayOptions</c>.
/// </summary>
public sealed class DocumentAnalysisOptions
{
    public const string Section = "DocumentAnalysis";

    /// <summary>A PDF page with fewer extractable characters than this is treated as image-only.</summary>
    [Range(0, int.MaxValue)]
    public int MinCharsPerPage { get; set; } = 50;

    /// <summary>Share of undecodable characters (control, private-use, U+FFFD) above which a text layer is treated as broken.</summary>
    [Range(0.0, 1.0)]
    public double MaxGarbageRatio { get; set; } = 0.2;

    /// <summary>Share of letters/digits among non-space characters below which a text layer is treated as broken.</summary>
    [Range(0.0, 1.0)]
    public double MinAlphanumericRatio { get; set; } = 0.5;

    /// <summary>A page with at least this many vector paths (table rules, drawings) is treated as layout-heavy.</summary>
    [Range(1, int.MaxValue)]
    public int LayoutMinPaths { get; set; } = 50;

    /// <summary>An image covering at least this share of the page makes it a scan (with an OCR layer if it also has text).</summary>
    [Range(0.0, 1.0)]
    public double FullPageImageCoverage { get; set; } = 0.85;

    [Range(36, 600)]
    public int PngDpi { get; set; } = 150;

    /// <summary>Upper bound for native-resolution rendering of fax / high-resolution scan pages.</summary>
    [Range(36, 600)]
    public int MaxRenderDpi { get; set; } = 300;

    /// <summary>Upper bound on pages rendered to PNG per request, embedded documents included, to bound CPU time and response size.</summary>
    [Range(1, 1000)]
    public int MaxPages { get; set; } = 20;

    /// <summary>PDFs with more pages are rejected: classification inspects every page.</summary>
    [Range(1, 100_000)]
    public int MaxPdfPages { get; set; } = 2000;

    /// <summary>TIFFs with more pages (frames) are rejected.</summary>
    [Range(1, 10_000)]
    public int MaxImageFrames { get; set; } = 200;

    /// <summary>
    /// Upper bound on the total uncompressed size of an .xlsx/.docx package. The converters load the whole package,
    /// so a small, highly compressed file could otherwise expand to gigabytes in memory.
    /// </summary>
    [Range(1, 1L << 34)]
    public long MaxOfficeUncompressedBytes { get; set; } = 256 * 1024 * 1024;

    /// <summary>Upper bound on rows converted to markdown per worksheet.</summary>
    [Range(1, 1_000_000)]
    public int MaxSpreadsheetRows { get; set; } = 5000;

    /// <summary>Upper bound on embedded files classified and prepared per document.</summary>
    [Range(0, 1000)]
    public int MaxAttachments { get; set; } = 10;

    /// <summary>Ask for OCR of pages without a usable text layer. OCR can also be switched off for the whole service.</summary>
    public bool EnableOcr { get; set; } = true;
}

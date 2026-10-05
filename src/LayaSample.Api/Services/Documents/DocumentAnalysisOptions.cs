namespace LayaSample.Api.Services.Documents;

public sealed class DocumentAnalysisOptions
{
    public const string Section = "DocumentAnalysis";

    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>A PDF page with fewer extractable characters than this is treated as image-only.</summary>
    public int MinCharsPerPage { get; set; } = 50;

    /// <summary>Share of undecodable characters (control, private-use, U+FFFD) above which a text layer is treated as broken.</summary>
    public double MaxGarbageRatio { get; set; } = 0.2;

    /// <summary>Share of letters/digits among non-space characters below which a text layer is treated as broken.</summary>
    public double MinAlphanumericRatio { get; set; } = 0.5;

    /// <summary>A page with at least this many vector paths (table rules, drawings) is treated as layout-heavy.</summary>
    public int LayoutMinPaths { get; set; } = 50;

    /// <summary>An image covering at least this share of the page makes it a scan (with an OCR layer if it also has text).</summary>
    public double FullPageImageCoverage { get; set; } = 0.85;

    public int PngDpi { get; set; } = 150;

    /// <summary>Upper bound for native-resolution rendering of fax / high-resolution scan pages.</summary>
    public int MaxRenderDpi { get; set; } = 300;

    /// <summary>Upper bound on pages rendered to PNG, to keep responses a sane size.</summary>
    public int MaxPages { get; set; } = 20;

    /// <summary>Upper bound on rows converted to markdown per worksheet.</summary>
    public int MaxSpreadsheetRows { get; set; } = 5000;

    /// <summary>Upper bound on embedded files classified and prepared per document.</summary>
    public int MaxAttachments { get; set; } = 10;

    public bool EnableOcr { get; set; } = true;

    /// <summary>
    /// OCR model family. Only PP-OCRv5 Latin ships with the app; v6 models (including this default) are fetched by
    /// scripts/download-models.sh.
    /// </summary>
    public Ocr.OcrModel OcrModel { get; set; } = Ocr.OcrModel.PPOCRv6Medium;

    /// <summary>OCR input is downscaled so its longer side is at most this many pixels.</summary>
    public int OcrMaxSide { get; set; } = 2560;

    /// <summary>
    /// Base URL of the optional Python document service (layout-aware PDF to markdown via Docling).
    /// When unset, layout-heavy pages fall back to page image + text layer.
    /// </summary>
    public string? LayoutServiceUrl { get; set; }
}

/// <summary>A problem with the uploaded document that maps to a specific HTTP status.</summary>
public sealed class DocumentException(string message, int statusCode, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}

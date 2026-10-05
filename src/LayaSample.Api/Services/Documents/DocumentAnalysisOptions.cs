using System.ComponentModel.DataAnnotations;

namespace LayaSample.Api.Services.Documents;

public sealed class DocumentAnalysisOptions
{
    public const string Section = "DocumentAnalysis";

    [Range(1, 1L << 30)]
    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;

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

    public bool EnableOcr { get; set; } = true;

    /// <summary>OCR model family. Only PP-OCRv5 Latin ships with the app; v6 models are fetched by scripts/download-models.sh.</summary>
    public Ocr.OcrModel OcrModel { get; set; } = Ocr.OcrModel.PPOCRv5Latin;

    /// <summary>OCR input is downscaled so its longer side is at most this many pixels.</summary>
    [Range(256, 16384)]
    public int OcrMaxSide { get; set; } = 2560;

    /// <summary>
    /// Documents analysed at the same time. Analysis is CPU-bound (rendering, decoding, OCR), so more than the core
    /// count only adds contention. 0 means half the processor count (at least 1).
    /// </summary>
    [Range(0, 1024)]
    public int MaxConcurrentAnalyses { get; set; }

    /// <summary>Requests waiting for an analysis slot; further requests get 503 with Retry-After.</summary>
    [Range(0, 10_000)]
    public int MaxQueuedAnalyses { get; set; } = 20;

    /// <summary>A request (waiting time included) is cancelled with 504 after this long.</summary>
    [Range(1, 3600)]
    public int RequestTimeoutSeconds { get; set; } = 120;

    public int EffectiveMaxConcurrentAnalyses =>
        MaxConcurrentAnalyses > 0 ? MaxConcurrentAnalyses : Math.Max(1, Environment.ProcessorCount / 2);
}

/// <summary>A problem with the uploaded document that maps to a specific HTTP status.</summary>
public sealed class DocumentException(string message, int statusCode, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}

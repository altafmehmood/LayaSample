using System.ComponentModel.DataAnnotations;

namespace LayaSample.Rendering;

/// <summary>
/// Settings for rendering, decoding and OCR. Bound from the same <c>DocumentAnalysis</c> section as the API's
/// settings, so <c>DocumentAnalysis__OcrModel</c> means the same thing wherever the rendering runs.
/// </summary>
public sealed class RenderingOptions
{
    public const string Section = "DocumentAnalysis";

    public bool EnableOcr { get; set; } = true;

    /// <summary>OCR model family. Only PP-OCRv5 Latin ships with the app; v6 models are fetched by scripts/download-models.sh.</summary>
    public OcrModel OcrModel { get; set; } = OcrModel.PPOCRv5Latin;

    /// <summary>OCR input is downscaled so its longer side is at most this many pixels.</summary>
    [Range(256, 16384)]
    public int OcrMaxSide { get; set; } = 2560;

    // Renderer service only.

    /// <summary>Largest document the renderer accepts. Keep it at least the API's MaxFileBytes.</summary>
    [Range(1, 1L << 30)]
    public long MaxDocumentBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>Pages one render request may ask for.</summary>
    [Range(1, 10_000)]
    public int MaxPagesPerJob { get; set; } = 200;

    /// <summary>Render jobs running at once. 0 means half the processor count (at least 1).</summary>
    [Range(0, 1024)]
    public int MaxConcurrentJobs { get; set; }

    /// <summary>Jobs waiting for a slot; further jobs get 503 with Retry-After.</summary>
    [Range(0, 10_000)]
    public int MaxQueuedJobs { get; set; } = 20;

    public int EffectiveMaxConcurrentJobs => MaxConcurrentJobs > 0 ? MaxConcurrentJobs : Math.Max(1, Environment.ProcessorCount / 2);
}

using System.ComponentModel.DataAnnotations;

namespace DocIntake.Api.Services.Documents;

/// <summary>
/// The gateway's settings for document endpoints: how big an upload may be, how long a request may take, how many run at
/// once, and where the document service is. Bound from the <c>DocumentAnalysis</c> section, the same one the document
/// service reads its own limits from (<c>DocumentAnalysisOptions</c> there), so existing settings keep their names.
/// </summary>
public sealed class DocumentGatewayOptions
{
    public const string Section = "DocumentAnalysis";

    [Range(1, 1L << 30)]
    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>
    /// Base URL of the document service (DocIntake.DocumentService), which classifies, prepares, renders and OCRs
    /// documents in an isolated process. Required: the gateway never parses an upload itself.
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Set DocumentAnalysis:DocumentServiceUrl to the document service.")]
    [Url]
    public string? DocumentServiceUrl { get; set; }

    /// <summary>
    /// Documents analysed at the same time. The work happens in the document service, but each analysis holds a slot
    /// there for its whole length, so this bounds how much the gateway sends it. 0 means half the processor count
    /// (at least 1).
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

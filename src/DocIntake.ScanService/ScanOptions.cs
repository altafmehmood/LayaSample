using System.ComponentModel.DataAnnotations;

namespace DocIntake.ScanService;

/// <summary>Settings of the scan service, bound from the <c>Scan</c> section.</summary>
public sealed class ScanOptions
{
    public const string Section = "Scan";

    /// <summary>Where clamd listens. In the pod it is the container next to this one, so the loopback address.</summary>
    [Required]
    public string ClamdHost { get; set; } = "127.0.0.1";

    [Range(1, 65535)]
    public int ClamdPort { get; set; } = 3310;

    /// <summary>Connecting, sending the document and waiting for the verdict.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Largest document accepted. clamd refuses a stream above its own <c>StreamMaxLength</c> (25 MB by default), so the
    /// default is the same: a larger one would only come back as "no verdict".
    /// </summary>
    [Range(1, int.MaxValue)]
    public long MaxDocumentBytes { get; set; } = 26_214_400;
}

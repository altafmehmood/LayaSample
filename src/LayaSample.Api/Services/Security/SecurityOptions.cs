using System.ComponentModel.DataAnnotations;

namespace LayaSample.Api.Services.Security;

/// <summary>Settings for scanning uploads, bound from the <c>Security</c> section.</summary>
public sealed class SecurityOptions
{
    public const string Section = "Security";

    public MalwareOptions Malware { get; set; } = new();

    /// <summary>
    /// What a blocking finding does: <c>reject</c> (the default) refuses the request with 422, <c>flag</c> reports it in the
    /// response and processing goes on. Findings: active content, macros, executable attachments, hidden text, and
    /// instruction-like text in the document or in the model's answer.
    /// </summary>
    public string OnFinding { get; set; } = "reject";

    public bool RejectsFindings => !string.Equals(OnFinding, "flag", StringComparison.OrdinalIgnoreCase);
}

public sealed class MalwareOptions : IValidatableObject
{
    public const string None = "none";
    public const string Clamd = "clamd";

    /// <summary><c>clamd</c> (a ClamAV daemon, the default) or <c>none</c> (development and tests, where there is no daemon).</summary>
    public string Provider { get; set; } = Clamd;

    public string Host { get; set; } = "clamav";

    [Range(1, 65535)]
    public int Port { get; set; } = 3310;

    /// <summary>Connecting, sending the document and waiting for the verdict.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// <c>closed</c> (the default): while the scanner cannot be reached or gives no verdict, uploads are refused with 503
    /// and Retry-After. <c>open</c>: they go ahead unscanned, with a warning in the log.
    /// </summary>
    public string FailMode { get; set; } = "closed";

    public bool FailClosed => !string.Equals(FailMode, "open", StringComparison.OrdinalIgnoreCase);

    public bool Enabled => string.Equals(Provider, Clamd, StringComparison.OrdinalIgnoreCase);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled && !string.Equals(Provider, None, StringComparison.OrdinalIgnoreCase))
            yield return new ValidationResult($"Security:Malware:Provider '{Provider}' is not supported; use '{Clamd}' or '{None}'.", [nameof(Provider)]);
        if (Enabled && string.IsNullOrWhiteSpace(Host))
            yield return new ValidationResult("Security:Malware:Host is required when the provider is clamd.", [nameof(Host)]);
        if (!string.Equals(FailMode, "closed", StringComparison.OrdinalIgnoreCase) && !string.Equals(FailMode, "open", StringComparison.OrdinalIgnoreCase))
            yield return new ValidationResult($"Security:Malware:FailMode '{FailMode}' is not supported; use 'closed' or 'open'.", [nameof(FailMode)]);
    }
}

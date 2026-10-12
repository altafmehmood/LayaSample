using System.ComponentModel.DataAnnotations;

namespace DocIntake.Api.Services.Documents.Extraction;

/// <summary>
/// The gateway's settings for extraction: where the extraction service is, and how many extractions it lets in at once.
/// Bound from the <c>Extraction</c> section, the same one the extraction service reads its own settings from
/// (<c>ExtractionOptions</c> there: the model provider, its key, the models), so existing names are kept. The gateway
/// holds no key and no model.
/// </summary>
public sealed class ExtractionGatewayOptions : IValidatableObject
{
    public const string Section = "Extraction";

    /// <summary>
    /// Base URL of the extraction service (DocIntake.ExtractionService). Empty: extraction answers 503 "not configured",
    /// and everything else keeps working. (Not <c>[Url]</c>, which rejects the empty string an empty environment variable
    /// binds as.)
    /// </summary>
    public string? ServiceUrl { get; set; }

    [Range(1, 32)]
    public int MaxConcurrentExtractions { get; set; } = 4;

    [Range(0, 100)]
    public int MaxQueuedExtractions { get; set; } = 10;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(ServiceUrl) && !(Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            yield return new ValidationResult($"{Section}:ServiceUrl must be an http or https address, or empty.", [nameof(ServiceUrl)]);
    }
}

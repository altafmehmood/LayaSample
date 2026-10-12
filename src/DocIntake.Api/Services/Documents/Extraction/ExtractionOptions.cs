using System.ComponentModel.DataAnnotations;

namespace DocIntake.Api.Services.Documents.Extraction;

/// <summary>Settings for AI extraction, bound from the <c>Extraction</c> section.</summary>
public sealed class ExtractionOptions : IValidatableObject
{
    public const string Section = "Extraction";

    /// <summary>The only provider implemented so far.</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>Falls back to the OPENAI_API_KEY environment variable. Extraction answers 503 while there is no key.</summary>
    public string? ApiKey { get; set; }

    [Url]
    public string? Endpoint { get; set; }

    /// <summary>Model for documents sent as page images.</summary>
    [Required]
    public string VisionModel { get; set; } = "";

    /// <summary>Model for documents sent as text only.</summary>
    [Required]
    public string TextModel { get; set; } = "";

    [Range(1, 20)]
    public int MaxImagePages { get; set; } = 10;

    /// <summary>OpenAI image detail: low, high or auto.</summary>
    [RegularExpression("^(low|high|auto)$")]
    public string ImageDetail { get; set; } = "high";

    [Range(5, 600)]
    public int TimeoutSeconds { get; set; } = 120;

    [Range(0, 5)]
    public int MaxRetries { get; set; } = 2;

    [Range(256, 32000)]
    public int MaxOutputTokens { get; set; } = 4000;

    [Range(1000, 2_000_000)]
    public int MaxTextChars { get; set; } = 200_000;

    [Range(1, 32)]
    public int MaxConcurrentExtractions { get; set; } = 4;

    [Range(0, 100)]
    public int MaxQueuedExtractions { get; set; } = 10;

    public string? ResolveApiKey() =>
        !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey : Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.Equals(Provider, "openai", StringComparison.OrdinalIgnoreCase))
            yield return new ValidationResult($"Extraction:Provider '{Provider}' is not supported; only 'openai' is.", [nameof(Provider)]);
    }
}

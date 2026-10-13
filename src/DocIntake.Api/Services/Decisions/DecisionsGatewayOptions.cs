using System.ComponentModel.DataAnnotations;

namespace DocIntake.Api.Services.Decisions;

/// <summary>
/// Where the decisions service (Laya, the feedback model) is. Bound from the <c>Decisions</c> section. Empty: feedback
/// analysis answers 503 "not configured", and everything else keeps working. (Not <c>[Url]</c>, which rejects the empty
/// string an empty environment variable binds as.)
/// </summary>
public sealed class DecisionsGatewayOptions : IValidatableObject
{
    public const string Section = "Decisions";

    public string? ServiceUrl { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(ServiceUrl) && !(Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            yield return new ValidationResult($"{Section}:ServiceUrl must be an http or https address, or empty.", [nameof(ServiceUrl)]);
    }
}

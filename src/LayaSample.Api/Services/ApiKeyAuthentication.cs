using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using LayaSample.Api.Services.Documents;
using Microsoft.Extensions.Options;

namespace LayaSample.Api.Services;

/// <summary>
/// API keys for the endpoints that spend money (see <see cref="ApiKeyEndpointExtensions.RequireApiKey{TBuilder}"/>).
/// Several keys can be configured at once so one can be rotated without downtime.
/// </summary>
public sealed class ApiAccessOptions : IValidatableObject
{
    public const string Section = "Auth";
    public const string HeaderName = "X-Api-Key";
    public const int MinKeyLength = 24;

    public string[] ApiKeys { get; set; } = [];

    /// <summary>The configured keys without blank entries (an empty environment variable binds as an empty entry).</summary>
    public IReadOnlyList<string> ConfiguredKeys => ApiKeys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Messages name the position only, never the key.
        var i = 0;
        foreach (var key in ConfiguredKeys)
        {
            i++;
            if (key.Length < MinKeyLength)
                yield return new ValidationResult($"Auth:ApiKeys entry {i} is shorter than {MinKeyLength} characters; use a random key, e.g. `openssl rand -base64 32`.", [nameof(ApiKeys)]);
            else if (key.Any(char.IsWhiteSpace))
                yield return new ValidationResult($"Auth:ApiKeys entry {i} contains whitespace.", [nameof(ApiKeys)]);
        }
    }
}

/// <summary>Marks an endpoint that needs a valid <c>X-Api-Key</c> header.</summary>
public sealed class RequireApiKeyMetadata;

public static class ApiKeyEndpointExtensions
{
    public static TBuilder RequireApiKey<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequireApiKeyMetadata());
}

/// <summary>
/// Rejects requests to marked endpoints that lack a valid key. It runs before the request timeout and the rate limiter,
/// so an unauthenticated caller never has its body read, never takes a queue slot and never reaches the model provider.
/// With no keys configured it fails closed (503), except in Development, so a local run works without setup.
/// </summary>
public sealed class ApiKeyMiddleware(
    RequestDelegate next,
    IOptions<ApiAccessOptions> options,
    IHostEnvironment environment,
    DocumentMetrics metrics,
    ILogger<ApiKeyMiddleware> logger)
{
    private readonly byte[][] _configured = options.Value.ConfiguredKeys.Select(Hash).ToArray();

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RequireApiKeyMetadata>() is null)
        {
            await next(context);
            return;
        }

        if (_configured.Length == 0)
        {
            if (environment.IsDevelopment())
            {
                await next(context);
                return;
            }
            await Reject(context, StatusCodes.Status503ServiceUnavailable, "unconfigured", "api key authentication is not configured");
            return;
        }

        var supplied = context.Request.Headers[ApiAccessOptions.HeaderName];
        if (supplied.Count != 1 || string.IsNullOrEmpty(supplied[0]))
        {
            await Reject(context, StatusCodes.Status401Unauthorized, "missing", $"the {ApiAccessOptions.HeaderName} header is required");
            return;
        }

        // Every configured key is compared, with no early exit, so timing reveals neither which key matched nor how much of one.
        var suppliedHash = Hash(supplied[0]!);
        var valid = false;
        foreach (var key in _configured)
            valid |= CryptographicOperations.FixedTimeEquals(suppliedHash, key);

        if (!valid)
        {
            await Reject(context, StatusCodes.Status401Unauthorized, "invalid", "the API key is not valid");
            return;
        }

        await next(context);
    }

    private async Task Reject(HttpContext context, int status, string reason, string detail)
    {
        metrics.RecordAuthRejection(reason);
        logger.LogWarning("Rejected a request to {Path}: API key {Reason}", context.Request.Path.Value, reason);
        if (status == StatusCodes.Status401Unauthorized)
            context.Response.Headers.WWWAuthenticate = "ApiKey";
        await Results.Problem(detail: detail, statusCode: status).ExecuteAsync(context);
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}

using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace LayaSample.Api.Services.Usage;

/// <summary>Where model usage is kept. An empty <see cref="DatabasePath"/> switches tracking off.</summary>
public sealed class UsageOptions
{
    public const string Section = "Usage";

    /// <summary>SQLite file; a relative path is resolved against the content root. In containers it sits on the volume at /data.</summary>
    public string? DatabasePath { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(DatabasePath);
}

/// <summary>The caller's API key as the usage record knows it: a short hash prefix, never the key.</summary>
public sealed record ApiCaller(string Fingerprint, bool IsAdmin)
{
    public const string ItemKey = "laya.caller";

    public static readonly ApiCaller Anonymous = new("anonymous", false);

    /// <summary>Development with no keys configured: every request is allowed, so it may read everything.</summary>
    public static readonly ApiCaller Development = new("dev", true);

    /// <summary>First 12 hex characters of the SHA-256 of the key. The key is random and long, so this reveals nothing usable.</summary>
    public static string FingerprintOf(byte[] keyHash) => Convert.ToHexString(keyHash, 0, 6).ToLowerInvariant();

    public static string FingerprintOf(string key) => FingerprintOf(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
}

public static class ApiCallerExtensions
{
    public static ApiCaller GetCaller(this HttpContext context) =>
        context.Items.TryGetValue(ApiCaller.ItemKey, out var caller) && caller is ApiCaller known ? known : ApiCaller.Anonymous;
}

/// <summary>What the endpoint knows about the request, handed to the extractor so each model call can be attributed.</summary>
public sealed record UsageContext(ApiCaller Caller, string FileName, long FileBytes, string DocumentKind, int Pages, string? TraceId);

/// <summary>
/// One model call. Tokens are null when the call failed before the provider answered. <see cref="InputTokens"/> includes
/// the cached ones and <see cref="OutputTokens"/> the reasoning ones, as the provider counts them.
/// </summary>
public sealed record UsageRecord(
    DateTimeOffset Timestamp,
    string? TraceId,
    string ApiKeyFingerprint,
    string FileName,
    long FileBytes,
    string DocumentKind,
    int Pages,
    int Images,
    string Agent,
    string Model,
    int Attempt,
    long? InputTokens,
    long? CachedInputTokens,
    long? ReasoningTokens,
    long? OutputTokens,
    decimal? CostUsd,
    string? PriceKey,
    string Outcome,
    long DurationMs,
    long Id = 0);

public interface IUsageRecorder
{
    /// <summary>Queues a record. Never throws and never waits: usage tracking must not fail or slow an extraction.</summary>
    void Record(UsageRecord record);
}

public sealed class NoopUsageRecorder : IUsageRecorder
{
    public void Record(UsageRecord record) { }
}

/// <summary>Strips what a client-supplied file name should not carry into a record, a log line or a CSV cell.</summary>
public static class UsageFileName
{
    public const int MaxLength = 255;

    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unnamed";
        var leaf = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        var cleaned = new string(leaf.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > MaxLength)
            cleaned = cleaned[..MaxLength];
        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }
}

/// <summary>One tier of a model's list price, in USD per million tokens. A null limit is the open-ended top tier.</summary>
public sealed class PriceTier
{
    /// <summary>The tier applies to calls with at most this many input tokens.</summary>
    public long? MaxInputTokens { get; set; }
    public decimal InputPerMillion { get; set; }
    public decimal CachedInputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
}

public sealed class ModelPriceOptions
{
    public List<PriceTier> Tiers { get; set; } = [];
}

/// <summary>List prices from the <c>Extraction:Pricing</c> section, kept with the date they were read.</summary>
public sealed class PricingOptions
{
    public const string Section = "Extraction:Pricing";

    public string AsOf { get; set; } = "";
    public Dictionary<string, ModelPriceOptions> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record PricedUsage(decimal CostUsd, string PriceKey);

/// <summary>Turns token counts into a list-price cost. Not the invoice: batch, flex and discounts are not modelled.</summary>
public sealed class ModelPricing(IOptions<PricingOptions> options)
{
    private readonly PricingOptions _options = options.Value;

    /// <summary>Null when the model has no configured price.</summary>
    public PricedUsage? Price(string model, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        if (!_options.Models.TryGetValue(model, out var prices) || prices.Tiers.Count == 0)
            return null;

        // The cheapest tier that still covers this call's input; beyond the last limit, the last tier applies.
        var tiers = prices.Tiers.OrderBy(t => t.MaxInputTokens ?? long.MaxValue).ToList();
        var tier = tiers.FirstOrDefault(t => inputTokens <= (t.MaxInputTokens ?? long.MaxValue)) ?? tiers[^1];

        // Cached tokens are part of the input count, billed at their own rate instead of the full one.
        var cached = Math.Clamp(cachedInputTokens, 0, inputTokens);
        var cost = ((inputTokens - cached) * tier.InputPerMillion + cached * tier.CachedInputPerMillion + outputTokens * tier.OutputPerMillion) / 1_000_000m;
        return new PricedUsage(Math.Round(cost, 6, MidpointRounding.AwayFromZero), $"{model}@{_options.AsOf}");
    }
}

using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace DocIntake.Api.Services.Usage;

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
    public const string ItemKey = "intake.caller";

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

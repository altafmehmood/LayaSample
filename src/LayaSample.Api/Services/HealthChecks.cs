using System.Text.Json;
using LayaSample.Api.Services.Documents.Ocr;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LayaSample.Api.Services;

/// <summary>
/// Readiness per model. Still loading is Unhealthy (keep traffic away while starting up). Failed is Degraded: the
/// instance still serves everything that does not need that model, so it should stay in rotation.
/// </summary>
public sealed class LayaHealthCheck(ModelWarmup warmup) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(HealthChecks.FromState(warmup.State, "feedback analysis"));
}

public sealed class OcrHealthCheck(OcrWarmup warmup) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(warmup.Enabled
            ? HealthChecks.FromState(warmup.State, "OCR of scanned pages and images")
            : HealthCheckResult.Healthy("disabled"));
}

public static class HealthChecks
{
    public const string Ready = "ready";

    public static HealthCheckResult FromState(WarmupState state, string feature) => state switch
    {
        WarmupState.Ready => HealthCheckResult.Healthy("ready"),
        WarmupState.Loading => HealthCheckResult.Unhealthy("loading"),
        _ => HealthCheckResult.Degraded($"failed to load; {feature} is unavailable")
    };

    /// <summary>Writes <c>{ "status": "Healthy", "checks": { "laya": { "status": ..., "description": ... } } }</c>.</summary>
    public static Task WriteJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(context.Response.Body, new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), description = e.Value.Description })
        }, cancellationToken: context.RequestAborted);
    }
}

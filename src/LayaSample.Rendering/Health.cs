using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LayaSample.Rendering;

public enum WarmupState { Loading, Ready, Failed }

/// <summary>Readiness of the OCR models (healthy when OCR is disabled).</summary>
public sealed class OcrHealthCheck(OcrWarmup warmup) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(warmup.Enabled
            ? HealthReporting.FromState(warmup.State, "OCR of scanned pages and images")
            : HealthCheckResult.Healthy("disabled"));
}

public static class HealthReporting
{
    /// <summary>Tag of the checks behind <c>/health/ready</c>.</summary>
    public const string Ready = "ready";

    /// <summary>
    /// Still loading is Unhealthy (keep traffic away while starting up). Failed is Degraded: the instance still serves
    /// everything that does not need that model, so it should stay in rotation.
    /// </summary>
    public static HealthCheckResult FromState(WarmupState state, string feature) => state switch
    {
        WarmupState.Ready => HealthCheckResult.Healthy("ready"),
        WarmupState.Loading => HealthCheckResult.Unhealthy("loading"),
        _ => HealthCheckResult.Degraded($"failed to load; {feature} is unavailable")
    };

    /// <summary>Writes <c>{ "status": "Healthy", "checks": { "ocr": { "status": ..., "description": ... } } }</c>.</summary>
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

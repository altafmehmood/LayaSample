using LayaSample.Rendering;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LayaSample.Api.Services;

public sealed class LayaHealthCheck(ModelWarmup warmup) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(HealthReporting.FromState(warmup.State, "feedback analysis"));
}

/// <summary>
/// The renderer service's own readiness. A renderer that is down or not ready makes this instance Degraded, not
/// Unhealthy: text PDFs, Office files and feedback analysis still work, and the renderer is usually shared, so taking
/// every API instance out of rotation would turn a partial outage into a full one.
/// </summary>
public sealed class RendererHealthCheck(RemotePageRasterizer renderer, ILogger<RendererHealthCheck> logger) : IHealthCheck
{
    private string? _last;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var result = await renderer.GetReadinessAsync(ct) switch
        {
            null => HealthCheckResult.Degraded("unreachable; scanned pages, images and page images are unavailable"),
            "Healthy" => HealthCheckResult.Healthy("ready"),
            var status => HealthCheckResult.Degraded($"renderer is {status}")
        };
        // The probe runs every few seconds; only a change is an event.
        var now = $"{result.Status}: {result.Description}";
        if (Interlocked.Exchange(ref _last, now) is { } before && before != now)
            logger.Log(result.Status == HealthStatus.Healthy ? LogLevel.Information : LogLevel.Warning,
                "Renderer readiness changed from {Before} to {Now}", before, now);
        return result;
    }
}

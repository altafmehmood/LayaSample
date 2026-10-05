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
public sealed class RendererHealthCheck(RemotePageRasterizer renderer) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await renderer.GetReadinessAsync(ct) switch
        {
            null => HealthCheckResult.Degraded("unreachable; scanned pages, images and page images are unavailable"),
            "Healthy" => HealthCheckResult.Healthy("ready"),
            var status => HealthCheckResult.Degraded($"renderer is {status}")
        };
}

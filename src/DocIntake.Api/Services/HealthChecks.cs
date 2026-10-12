using DocIntake.ServiceDefaults;
using DocIntake.Api.Services.Documents;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocIntake.Api.Services;

public sealed class LayaHealthCheck(ModelWarmup warmup) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(HealthReporting.FromState(warmup.State, "feedback analysis"));
}

/// <summary>
/// The document service's own readiness. A document service that is down or not ready makes this instance Degraded, not
/// Unhealthy: feedback analysis still works, and the service is usually shared, so taking every gateway instance out of
/// rotation would turn a partial outage into a full one.
/// </summary>
public sealed class DocumentServiceHealthCheck(DocumentServiceClient client, ILogger<DocumentServiceHealthCheck> logger) : IHealthCheck
{
    private string? _last;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var result = await client.GetReadinessAsync(ct) switch
        {
            null => HealthCheckResult.Degraded("unreachable; document analysis and extraction are unavailable"),
            "Healthy" => HealthCheckResult.Healthy("ready"),
            var status => HealthCheckResult.Degraded($"document service is {status}")
        };
        // The probe runs every few seconds; only a change is an event.
        var now = $"{result.Status}: {result.Description}";
        if (Interlocked.Exchange(ref _last, now) is { } before && before != now)
            logger.Log(result.Status == HealthStatus.Healthy ? LogLevel.Information : LogLevel.Warning,
                "Document service readiness changed from {Before} to {Now}", before, now);
        return result;
    }
}

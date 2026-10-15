using DocIntake.ServiceDefaults;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocIntake.Api.Services;

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
            logger.ReadinessChanged(result.Status == HealthStatus.Healthy ? LogLevel.Information : LogLevel.Warning, "Document service", before, now);
        return result;
    }
}

/// <summary>
/// The extraction service's readiness. Degraded when it does not answer: uploads that are only analysed still work, and
/// only extraction is affected. Not configuring a service URL at all is a choice, not an outage, and reports Healthy.
/// </summary>
public sealed class ExtractionServiceHealthCheck(ExtractionServiceClient client, Microsoft.Extensions.Options.IOptions<ExtractionGatewayOptions> options,
    ILogger<ExtractionServiceHealthCheck> logger) : IHealthCheck
{
    private string? _last;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var result = string.IsNullOrWhiteSpace(options.Value.ServiceUrl)
            ? HealthCheckResult.Healthy("not configured: Extraction:ServiceUrl is empty; extraction answers 503")
            : await client.GetReadinessAsync(ct) switch
            {
                null => HealthCheckResult.Degraded("unreachable; extraction is unavailable"),
                "Healthy" => HealthCheckResult.Healthy("ready"),
                var status => HealthCheckResult.Degraded($"extraction service is {status}")
            };
        var now = $"{result.Status}: {result.Description}";
        if (Interlocked.Exchange(ref _last, now) is { } before && before != now)
            logger.ReadinessChanged(result.Status == HealthStatus.Healthy ? LogLevel.Information : LogLevel.Warning, "Extraction service", before, now);
        return result;
    }
}

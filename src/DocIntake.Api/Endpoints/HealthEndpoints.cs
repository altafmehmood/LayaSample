using DocIntake.ServiceDefaults;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace DocIntake.Api.Endpoints;

internal static class HealthEndpoints
{
    /// <summary>
    /// Liveness: the process is up. Readiness: the checks tagged ready, which report <c>Degraded</c> (still 200) rather than
    /// <c>Unhealthy</c> when a service the gateway depends on is down or loading, so the gateway stays in rotation.
    /// </summary>
    public static void MapGatewayHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = HealthReporting.WriteJson });
        var readiness = new HealthCheckOptions { Predicate = c => c.Tags.Contains(HealthReporting.Ready), ResponseWriter = HealthReporting.WriteJson };
        app.MapHealthChecks("/health/ready", readiness);
        app.MapHealthChecks("/health", readiness);
    }
}

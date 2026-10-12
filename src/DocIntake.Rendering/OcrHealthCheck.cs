using Microsoft.Extensions.Diagnostics.HealthChecks;
using DocIntake.ServiceDefaults;

namespace DocIntake.Rendering;

/// <summary>Readiness of the OCR models (healthy when OCR is disabled).</summary>
public sealed class OcrHealthCheck(OcrWarmup warmup) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(warmup.Enabled
            ? HealthReporting.FromState(warmup.State, "OCR of scanned pages and images")
            : HealthCheckResult.Healthy("disabled"));
}

using DocIntake.Contracts;
using DocIntake.ServiceDefaults;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DocIntake.ScanService;

/// <summary>
/// The scan service: a thin HTTP front for the ClamAV daemon that runs in the same pod. The gateway streams a document to
/// <c>POST /scan</c> and gets a verdict; clamd's own plaintext socket never leaves the pod. It parses nothing, so a malicious
/// document is only ever bytes here.
/// </summary>
/// <remarks>A named entry point rather than top-level statements, so tests can host it next to the gateway's Program.</remarks>
public sealed class ScanServiceEntryPoint
{
    public static void Main(string[] args) => Build(args).Run();

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddIntakeTelemetry("docintake-scan");

        builder.Services.AddProblemDetails();
        builder.Services.AddOptions<ScanOptions>()
            .Bind(builder.Configuration.GetSection(ScanOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton<ClamdClient>();
        builder.Services.AddHealthChecks().AddCheck<ClamdHealthCheck>("clamd", tags: [HealthReporting.Ready], timeout: TimeSpan.FromSeconds(8));

        // The body limit is fixed when the server is built, so the option is read here; ValidateOnStart still checks it.
        var options = builder.Configuration.GetSection(ScanOptions.Section).Get<ScanOptions>() ?? new();
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxDocumentBytes);

        var app = builder.Build();

        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError
        });
        app.UseStatusCodePages();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = HealthReporting.WriteJson });
        app.MapHealthChecks("/" + ScanRoutes.Ready, new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains(HealthReporting.Ready),
            ResponseWriter = HealthReporting.WriteJson
        });

        // The document is the request body, read once and passed on in chunks: it is never held in memory or written to disk.
        // A verdict of "unavailable" is still a 200: the service worked, the scanner behind it did not. The gateway decides
        // what that means (Security:Malware:FailMode).
        app.MapPost("/" + ScanRoutes.Scan, async (HttpRequest request, ClamdClient clamd, ILogger<ScanServiceEntryPoint> logger, CancellationToken ct) =>
        {
            var verdict = await clamd.ScanAsync(request.Body, ct);
            logger.Scanned(request.ContentLength, verdict.Outcome);
            return Results.Json(verdict, IntakeJson.Options);
        });

        return app;
    }
}

/// <summary>Unhealthy while clamd does not answer a PING: nothing can be scanned, so the pod should not be sent uploads.</summary>
public sealed class ClamdHealthCheck(ClamdClient clamd, ILogger<ClamdHealthCheck> logger) : IHealthCheck
{
    private string? _last;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var result = await clamd.PingAsync(ct)
            ? HealthCheckResult.Healthy("clamd ready")
            : HealthCheckResult.Unhealthy("clamd does not answer (its signature database may still be loading)");
        var now = $"{result.Status}: {result.Description}";
        if (Interlocked.Exchange(ref _last, now) is { } before && before != now)
            logger.ReadinessChanged(result.Status == HealthStatus.Healthy ? LogLevel.Information : LogLevel.Warning, "clamd", before, now);
        return result;
    }
}

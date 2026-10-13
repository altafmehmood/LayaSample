using System.Threading.RateLimiting;
using DocIntake.Contracts;
using DocIntake.ServiceDefaults;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DocIntake.ExtractionService;

/// <summary>
/// The extraction service: the only process that holds the model provider's key and reaches the internet. It turns a
/// prepared document into structured data and reports every model call it made, so the gateway can record the usage.
/// It never sees the caller, the file's name or the API keys. Deploy it with egress to the provider and nothing else.
/// </summary>
/// <remarks>A named entry point rather than top-level statements, so tests can host it next to the gateway's Program.</remarks>
public sealed class ExtractionServiceEntryPoint
{
    private const string ExtractionPolicy = "extraction";

    public static void Main(string[] args) => Build(args).Run();

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddIntakeTelemetry("docintake-extraction", ExtractionMetrics.MeterName);

        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        builder.Services.AddExtraction(builder.Configuration);
        builder.Services.AddHealthChecks().AddCheck<ExtractionHealthCheck>("extraction", tags: [HealthReporting.Ready]);

        // Limits are fixed when the pipeline is built, so they are read here; ValidateOnStart still checks them.
        var options = builder.Configuration.GetSection(ExtractionOptions.Section).Get<ExtractionOptions>() ?? new();
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxRequestBytes);
        builder.Services.AddRateLimiter(o =>
        {
            // Mostly waiting on the provider, so its own slots. The gateway limits too, but it can be replicated.
            o.AddConcurrencyLimiter(ExtractionPolicy, l =>
            {
                l.PermitLimit = options.MaxConcurrentExtractions;
                l.QueueLimit = options.MaxQueuedExtractions;
                l.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
            o.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
            o.OnRejected = (context, _) =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILogger<ExtractionServiceEntryPoint>>()
                    .RateLimited();
                context.HttpContext.Response.Headers.RetryAfter = "10";
                return context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = { Status = StatusCodes.Status503ServiceUnavailable, Detail = "extraction is busy; retry later" }
                });
            };
        });

        var app = builder.Build();

        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError
        });
        app.UseStatusCodePages();
        app.UseRateLimiter();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = HealthReporting.WriteJson });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains(HealthReporting.Ready),
            ResponseWriter = HealthReporting.WriteJson
        });

        // Lets the gateway answer "extraction is not configured" before it reads, scans and prepares an upload.
        app.MapGet("/extraction/status", (ExtractionOrchestrator extractor) => Results.Json(new ExtractionStatus(extractor.IsConfigured), IntakeJson.Options));

        app.MapPost("/extract", async (ExtractRequest request, ExtractionOrchestrator extractor, ILogger<ExtractionServiceEntryPoint> logger,
            CancellationToken ct) =>
        {
            if (!extractor.IsConfigured)
                return Results.Problem(detail: "extraction is not configured", statusCode: StatusCodes.Status503ServiceUnavailable);
            try
            {
                return Results.Json(await extractor.ExtractAsync(request, ct), IntakeJson.Options);
            }
            catch (IntakeFailureException ex)
            {
                // Not the exception itself: provider error text can echo parts of the API key or of the document.
                logger.ExtractionFailed(ex.Kind, ex.Message, ex.InnerException?.GetType().Name ?? "none", ex.Calls.Count);
                return ServiceProblems.FromException(ex)!;
            }
        })
        .RequireRateLimiting(ExtractionPolicy);

        return app;
    }
}

/// <summary>Healthy either way: without a key the service is running and says so, and answers extraction with 503.</summary>
public sealed class ExtractionHealthCheck(ExtractionOrchestrator extractor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(HealthCheckResult.Healthy(extractor.IsConfigured ? "configured" : "not configured: no model provider key; extraction answers 503"));
}

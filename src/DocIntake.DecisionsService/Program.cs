using DocIntake.Contracts;
using DocIntake.ServiceDefaults;
using ElBruno.LocalLLMs.Decisions;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocIntake.DecisionsService;

/// <summary>
/// The decisions service: Laya, the local model that scores hotel-guest feedback for dissatisfaction. It needs no network
/// once the model is on disk (the images carry it), so deploy it without egress. It loads the model in the background and
/// says so while it does, so a slow load never holds anything else back.
/// </summary>
/// <remarks>A named entry point rather than top-level statements, so tests can host it next to the gateway's Program.</remarks>
public sealed class DecisionsServiceEntryPoint
{
    public static void Main(string[] args) => Build(args).Run();

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddIntakeTelemetry("docintake-decisions", DecisionsMetrics.MeterName);

        builder.Services.AddProblemDetails();
        builder.Services.AddLocalDecisions(o =>
        {
            builder.Configuration.GetSection("Laya").Bind(o);
            // Resolve a relative cache path against the project folder, not the working directory.
            if (!string.IsNullOrEmpty(o.CacheDirectory))
                o.CacheDirectory = Path.GetFullPath(o.CacheDirectory, builder.Environment.ContentRootPath);
        });
        builder.Services.AddSingleton<DecisionsMetrics>();
        builder.Services.AddSingleton<IDissatisfactionAnalyzer, DissatisfactionAnalyzer>();
        builder.Services.AddSingleton<ModelWarmup>();
        builder.Services.AddSingleton<IModelStatus>(sp => sp.GetRequiredService<ModelWarmup>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelWarmup>());
        builder.Services.AddHealthChecks().AddCheck<LayaHealthCheck>("laya", tags: [HealthReporting.Ready]);

        var app = builder.Build();

        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError
        });
        app.UseStatusCodePages();

        // Liveness: the process is up. Readiness: the model is loaded (Degraded, still 200, when it failed for good).
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = HealthReporting.WriteJson });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains(HealthReporting.Ready),
            ResponseWriter = HealthReporting.WriteJson
        });

        app.MapPost("/decisions/feedback", async (AnalyzeRequest request, IDissatisfactionAnalyzer analyzer, IModelStatus model, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return Results.Problem(detail: "text is required", statusCode: StatusCodes.Status400BadRequest);
            if (request.Text.Length > FeedbackLimits.MaxTextLength)
                return Results.Problem(detail: $"text must be at most {FeedbackLimits.MaxTextLength} characters", statusCode: StatusCodes.Status400BadRequest);
            if (model.State != WarmupState.Ready)
                return ServiceProblems.FromException(new IntakeFailureException(FailureKind.DependencyUnavailable,
                    model.State == WarmupState.Failed ? "model failed to load" : "model is still loading"))!;

            return Results.Json(await analyzer.AnalyzeAsync(request.Text, ct), IntakeJson.Options);
        });

        return app;
    }
}

public sealed class LayaHealthCheck(IModelStatus model) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(HealthReporting.FromState(model.State, "feedback analysis"));
}

using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DocIntake.ServiceDefaults;

/// <summary>
/// OpenTelemetry for both services. Instrumentation is always on, so trace context flows from the API to the renderer;
/// data leaves the process only when an OTLP endpoint is configured (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, with
/// <c>OTEL_EXPORTER_OTLP_HEADERS</c> for credentials). In the chart both services send to an in-cluster collector, the
/// only pod allowed to reach the backend.
/// </summary>
public static class IntakeTelemetry
{
    /// <summary>Spans started by the services' own code.</summary>
    public const string SourceName = "DocIntake";

    /// <summary>Gen-AI spans of the extraction chat clients.</summary>
    public const string GenAiSourceName = "DocIntake.GenAI";

    public static readonly ActivitySource Source = new(SourceName, Version);

    /// <summary>The build's version (the image tag in the chart sets <c>service.version</c> when it differs).</summary>
    public static string Version { get; } =
        typeof(IntakeTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    // Built-in meters of ASP.NET Core and HttpClient: connections, request queues, rate-limit leases, handler exceptions, DNS.
    private static readonly string[] PlatformMeters =
    [
        "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.RateLimiting",
        "Microsoft.AspNetCore.Diagnostics", "Microsoft.AspNetCore.Routing", "System.Net.Http", "System.Net.NameResolution"
    ];

    /// <param name="meters">The service's own meters; the platform ones above are always added.</param>
    public static WebApplicationBuilder AddIntakeTelemetry(this WebApplicationBuilder builder, string serviceName, params string[] meters)
    {
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: Version)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSource(SourceName, GenAiSourceName))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(PlatformMeters)
                .AddMeter(meters))
            .WithLogging();

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            otel.UseOtlpExporter();
        return builder;
    }

    /// <summary>Marks the activity failed and records the exception (type, message, stack) on it.</summary>
    public static void RecordFailure(this Activity? activity, Exception ex, string? kind = null)
    {
        if (activity is null) return;
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddException(ex);
        if (kind is not null) activity.SetTag("intake.failure.kind", kind);
    }
}

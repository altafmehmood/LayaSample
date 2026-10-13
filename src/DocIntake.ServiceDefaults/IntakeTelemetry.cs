using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using DocIntake.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DocIntake.ServiceDefaults;

/// <summary>
/// OpenTelemetry for every service. Instrumentation is always on, so trace context flows from the gateway to the services;
/// data leaves the process only when an OTLP endpoint is configured (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, with
/// <c>OTEL_EXPORTER_OTLP_HEADERS</c> for credentials). In the chart every service sends to an in-cluster collector, the
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
        builder.AddIntakeLogging();
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: Version)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSource(SourceName, GenAiSourceName))
            .WithMetrics(m => m
                // A histogram point keeps the trace id of a sampled request, so a slow bucket links to a trace in Honeycomb.
                .SetExemplarFilter(ExemplarFilterType.TraceBased)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(PlatformMeters)
                .AddMeter(meters))
            // Scopes (endpoint, caller) and the formatted message travel with every exported log record.
            .WithLogging(null, o =>
            {
                o.IncludeScopes = true;
                o.IncludeFormattedMessage = true;
            });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            otel.UseOtlpExporter();
        return builder;
    }

    /// <summary>
    /// Console logs that carry the trace: <c>TraceId</c>, <c>SpanId</c> and <c>ParentId</c> on every scope, and outside Development
    /// one JSON object per line with the scopes, so a line in <c>kubectl logs</c> can be found in Honeycomb and back.
    /// </summary>
    public static WebApplicationBuilder AddIntakeLogging(this WebApplicationBuilder builder)
    {
        builder.Logging.Configure(o => o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        if (!builder.Environment.IsDevelopment())
        {
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
        }
        return builder;
    }

    /// <summary>Marks the activity failed and records the exception (type, message, stack) on it.</summary>
    public static void RecordFailure(this Activity? activity, Exception ex, string? kind = null)
    {
        if (activity is null) return;
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddException(ex);
        // Low-cardinality, so it can be grouped on: the failure kind for the services' own failures, else the exception type.
        activity.SetTag("error.type", ex is IntakeFailureException failure ? failure.Kind.ToString() : ex.GetType().FullName);
        if (kind is not null) activity.SetTag("intake.failure.kind", kind);
    }
}

/// <summary>
/// Bucket boundaries for histograms. The SDK's defaults (0 to 10000, tuned for milliseconds) put nearly every value of a
/// histogram in seconds into the first bucket; these span 5 ms to 2 minutes, which is what these services take.
/// </summary>
public static class IntakeBuckets
{
    public static InstrumentAdvice<double> Seconds { get; } = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120]
    };

    public static InstrumentAdvice<long> Bytes { get; } = new()
    {
        HistogramBucketBoundaries = [1_024, 10_240, 102_400, 524_288, 1_048_576, 5_242_880, 10_485_760, 20_971_520, 52_428_800]
    };

    public static InstrumentAdvice<int> Pages { get; } = new()
    {
        HistogramBucketBoundaries = [1, 2, 3, 5, 10, 20, 50, 100]
    };
}

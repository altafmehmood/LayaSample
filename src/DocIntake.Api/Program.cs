using DocIntake.Api;
using DocIntake.Api.Endpoints;
using DocIntake.Api.Services;
using DocIntake.Api.Services.Decisions;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using DocIntake.Api.Services.Security;
using DocIntake.Api.Services.Usage;
using DocIntake.Contracts;
using DocIntake.ServiceDefaults;
using Scalar.AspNetCore;

// The gateway: the public API. It authenticates, rate-limits, scans, applies the risk policy and records usage, and calls the
// document, extraction and decisions services for everything that reads a document, calls a model provider or runs a model.
// This file only wires it together; the endpoints, filters and settings are in their own files.

var builder = WebApplication.CreateBuilder(args);

builder.AddIntakeTelemetry("docintake-gateway", DocumentMetrics.MeterName);

builder.Services.AddGatewayOpenApi(builder.Configuration);
builder.Services.AddOptions<ApiAccessOptions>()
    .Bind(builder.Configuration.GetSection(ApiAccessOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemMapping>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<IDocumentRouter, DocumentRouter>();
builder.Services.AddSingleton<DocumentMetrics>();
builder.Services.AddSingleton<DocumentPipeline>();
builder.Services.AddUsageTracking(builder.Configuration);
foreach (var route in DocumentRoutes.All)
    builder.Services.AddSingleton<IDocumentAgent>(new StubDocumentAgent(route));

var health = builder.Services.AddHealthChecks();
builder.Services.AddMalwareScanning(builder.Configuration, health);
builder.Services.AddDocumentServiceClient(builder.Configuration, health);
builder.Services.AddExtractionServiceClient(builder.Configuration, health);
builder.Services.AddDecisionsServiceClient(builder.Configuration, health);

// Endpoint limits are fixed when the app is built, so the options are read here; ValidateOnStart still checks them.
var documentOptions = builder.Configuration.GetSection(DocumentGatewayOptions.Section).Get<DocumentGatewayOptions>() ?? new();
var extractionOptions = builder.Configuration.GetSection(ExtractionGatewayOptions.Section).Get<ExtractionGatewayOptions>() ?? new();
var decisionsOptions = builder.Configuration.GetSection(DecisionsGatewayOptions.Section).Get<DecisionsGatewayOptions>() ?? new();
builder.Services.AddGatewayRateLimiting(documentOptions, extractionOptions);

var app = builder.Build();

// A failure the gateway knows (ProblemMapping, registered above) is answered there; malformed requests (a broken multipart
// body) keep their 400 rather than becoming a 500.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError
});
app.UseStatusCodePages();
// Before the timeout and the rate limiter, so a rejected caller never has its body read or takes a slot.
app.UseMiddleware<ApiKeyMiddleware>();
// Timeouts run first so time spent queued for an analysis slot counts against the request.
app.UseRequestTimeouts();
app.UseRateLimiter();

app.MapOpenApi();            // /openapi/v1.json
app.MapScalarApiReference(); // /scalar/v1
app.MapGatewayHealth();
app.MapFeedbackEndpoints();
app.MapDocumentEndpoints(documentOptions);
app.MapUsageEndpoints();

// One line that says how this instance was configured, so a trace can be read against it. No secrets.
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation(
    "DocIntake API {Version} started in {Environment}: document service {DocumentService}, extraction service {Extraction}, decisions service {Decisions}, "
    + "{Analyses} analyses and {Extractions} extractions at once, telemetry export {Telemetry}",
    IntakeTelemetry.Version, app.Environment.EnvironmentName,
    documentOptions.DocumentServiceUrl,
    string.IsNullOrWhiteSpace(extractionOptions.ServiceUrl) ? "not configured" : extractionOptions.ServiceUrl,
    string.IsNullOrWhiteSpace(decisionsOptions.ServiceUrl) ? "not configured" : decisionsOptions.ServiceUrl,
    documentOptions.EffectiveMaxConcurrentAnalyses, extractionOptions.MaxConcurrentExtractions,
    string.IsNullOrWhiteSpace(app.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]) ? "off" : "on"));

app.Run();

public partial class Program;

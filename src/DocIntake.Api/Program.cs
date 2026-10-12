using DocIntake.ServiceDefaults;
using System.Diagnostics;
using System.Threading.RateLimiting;
using ElBruno.LocalLLMs.Decisions;
using DocIntake.Api;
using DocIntake.Contracts;
using DocIntake.Api.Services;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using DocIntake.Api.Services.Security;
using DocIntake.Api.Services.Usage;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddIntakeTelemetry("docintake-gateway", DocumentMetrics.MeterName);

builder.Services.AddLocalDecisions(o =>
{
    builder.Configuration.GetSection("Laya").Bind(o);
    // Resolve a relative cache path against the project folder, not the working directory.
    if (!string.IsNullOrEmpty(o.CacheDirectory))
        o.CacheDirectory = Path.GetFullPath(o.CacheDirectory, builder.Environment.ContentRootPath);
});
builder.Services.AddOpenApi(o =>
{
    o.AddDocumentTransformer((document, _, _) =>
    {
        // Behind a gateway the request URL is not the public one; OpenApi:ServerUrl says what clients (Scalar) should call.
        var serverUrl = builder.Configuration["OpenApi:ServerUrl"];
        if (!string.IsNullOrWhiteSpace(serverUrl))
            document.Servers = [new() { Url = serverUrl }];

        // Lets Scalar ask for the key; it is attached to the endpoints that need it by the operation transformer below.
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiAccessOptions.HeaderName,
            Description = "Access key for the endpoints that spend model-provider money."
        };
        return Task.CompletedTask;
    });
    o.AddOperationTransformer((operation, context, _) =>
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<RequireApiKeyMetadata>().Any())
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", context.Document)] = [] });
        }
        return Task.CompletedTask;
    });
});
builder.Services.AddOptions<ApiAccessOptions>()
    .Bind(builder.Configuration.GetSection(ApiAccessOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IDissatisfactionAnalyzer, DissatisfactionAnalyzer>();
builder.Services.AddOptions<DocumentGatewayOptions>()
    .Bind(builder.Configuration.GetSection(DocumentGatewayOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<IDocumentRouter, DocumentRouter>();
builder.Services.AddSingleton<DocumentMetrics>();
builder.Services.AddSingleton<DocumentPipeline>();
builder.Services.AddSingleton<ProblemMapping>();
builder.Services.AddUsageTracking(builder.Configuration);
builder.Services.AddExtraction(builder.Configuration);
foreach (var route in DocumentRoutes.All)
    builder.Services.AddSingleton<IDocumentAgent>(new StubDocumentAgent(route));
builder.Services.AddSingleton<ModelWarmup>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelWarmup>());
var healthChecks = builder.Services.AddHealthChecks().AddCheck<LayaHealthCheck>("laya", tags: [HealthReporting.Ready]);
builder.Services.AddMalwareScanning(builder.Configuration, healthChecks);

// Endpoint limits are fixed when the app is built, so the options are read here; ValidateOnStart still checks them (and
// refuses to start without a document service to send uploads to).
var documentOptions = builder.Configuration.GetSection(DocumentGatewayOptions.Section).Get<DocumentGatewayOptions>() ?? new();
var extractionOptions = builder.Configuration.GetSection(ExtractionOptions.Section).Get<ExtractionOptions>() ?? new();

// Untrusted bytes are read only inside the document service: classification, preparation, rendering and OCR all happen there.
builder.Services.AddHttpClient(DocumentServiceClient.HttpClientName, c =>
{
    if (!string.IsNullOrWhiteSpace(documentOptions.DocumentServiceUrl))
        c.BaseAddress = new Uri(documentOptions.DocumentServiceUrl.TrimEnd('/') + "/");
    // Bounded by the request's own token instead, which carries the request timeout.
    c.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<DocumentServiceClient>();
builder.Services.AddSingleton<IDocumentClassifier>(sp => sp.GetRequiredService<DocumentServiceClient>());
builder.Services.AddSingleton<IDocumentPreparer>(sp => sp.GetRequiredService<DocumentServiceClient>());
healthChecks.AddCheck<DocumentServiceHealthCheck>("document-service", tags: [HealthReporting.Ready], timeout: TimeSpan.FromSeconds(5));

const string DocumentsPolicy = "documents";
const string ExtractionPolicy = "extraction";
builder.Services.AddRequestTimeouts();
builder.Services.AddRateLimiter(o =>
{
    // Analysis is synchronous and CPU-bound: bound how many run at once instead of letting them starve the thread pool.
    o.AddConcurrencyLimiter(DocumentsPolicy, l =>
    {
        l.PermitLimit = documentOptions.EffectiveMaxConcurrentAnalyses;
        l.QueueLimit = documentOptions.MaxQueuedAnalyses;
        l.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    // Extraction mostly waits on the model provider, so it gets its own slots rather than competing with analysis.
    o.AddConcurrencyLimiter(ExtractionPolicy, l =>
    {
        l.PermitLimit = extractionOptions.MaxConcurrentExtractions;
        l.QueueLimit = extractionOptions.MaxQueuedExtractions;
        l.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    o.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
    o.OnRejected = (context, ct) =>
    {
        // The policy is the endpoint's metadata name; the metric says which pool ran dry.
        var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "unknown";
        var services = context.HttpContext.RequestServices;
        services.GetRequiredService<DocumentMetrics>().RecordRateLimited(policy);
        Telemetry.TagRequest(context.HttpContext, "intake.outcome", "rate_limited");
        Telemetry.TagRequest(context.HttpContext, "intake.ratelimit.policy", policy);
        services.GetRequiredService<ILoggerFactory>().CreateLogger("DocIntake.RateLimiting")
            .LogWarning("Rejected {Path}: every {Policy} slot and queue place is taken (waited {WaitedMs:F0} ms)",
                context.HttpContext.Request.Path.Value, policy, Telemetry.RequestAge(context.HttpContext).TotalMilliseconds);
        context.HttpContext.Response.Headers.RetryAfter = "10";
        return context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails = { Status = StatusCodes.Status503ServiceUnavailable, Detail = "too many documents are being analysed; retry later" }
        });
    };
});

var app = builder.Build();

// Malformed requests (e.g. a broken multipart body) keep their 400 rather than becoming a 500.
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

const int MaxTextLength = 4000;

static IResult Problem(int status, string detail) => ProblemMapping.Problem(status, detail);

// What the upload turned out to be, on the request's own span.
static void TagDocument(HttpContext context, DocumentClassification classification, PreparedDocument prepared)
{
    Telemetry.TagRequest(context, "intake.document.kind", classification.Kind.ToString());
    Telemetry.TagRequest(context, "intake.document.strategy", prepared.Strategy.ToString());
    Telemetry.TagRequest(context, "intake.document.pages", classification.PageCount);
    Telemetry.TagRequest(context, "intake.document.parts", prepared.Parts.Count);
}

// Liveness: the process is up. Readiness: the models are loaded (Degraded, still 200, when one failed for good).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = HealthReporting.WriteJson });
var readiness = new HealthCheckOptions { Predicate = c => c.Tags.Contains(HealthReporting.Ready), ResponseWriter = HealthReporting.WriteJson };
app.MapHealthChecks("/health/ready", readiness);
app.MapHealthChecks("/health", readiness);

app.MapPost("/api/feedback/analyze", async (AnalyzeRequest req, IDissatisfactionAnalyzer analyzer, ModelWarmup warmup, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Text))
        return Problem(StatusCodes.Status400BadRequest, "text is required");
    if (req.Text.Length > MaxTextLength)
        return Problem(StatusCodes.Status400BadRequest, $"text must be at most {MaxTextLength} characters");
    if (!warmup.Ready)
        return Problem(StatusCodes.Status503ServiceUnavailable,
            warmup.State == WarmupState.Failed ? "model failed to load" : "model is still loading");

    return Results.Ok(await analyzer.AnalyzeAsync(req.Text, ct));
})
.WithName("AnalyzeFeedback")
.WithSummary("Analyze hotel guest feedback for dissatisfaction")
.Produces<AnalyzeResponse>()
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Room for the multipart framing around the file, so an allowed file is never cut off by the body limit.
var maxBodyBytes = documentOptions.MaxFileBytes + 64 * 1024;

app.MapPost("/api/documents/analyze", async (
    IFormFile? file,
    bool? dispatch,
    bool? includeData,
    DocumentPipeline pipeline,
    ProblemMapping problems,
    IDocumentRouter router,
    IOptions<DocumentGatewayOptions> options,
    DocumentMetrics metrics,
    ILogger<Program> logger,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    if (file is null || file.Length == 0)
        return Problem(StatusCodes.Status400BadRequest, "file is required");
    if (file.Length > options.Value.MaxFileBytes)
        return Problem(StatusCodes.Status413PayloadTooLarge, $"file must be at most {options.Value.MaxFileBytes} bytes");

    var run = pipeline.NewRun();
    Telemetry.TagRequest(httpContext, "intake.file.bytes", file.Length);
    metrics.RecordQueueWait("analyze", Telemetry.RequestAge(httpContext));
    try
    {
        await pipeline.PrepareAsync(file, run, ct);
        var classification = run.Classification!;
        var prepared = run.Prepared!;
        TagDocument(httpContext, classification, prepared);
        var agent = dispatch == false ? null : await router.RouteAsync(classification, prepared, ct);
        run.Lap("route");
        Telemetry.TagRequest(httpContext, "intake.outcome", "ok");

        // Content only, never file names or text: both can carry personal data.
        logger.LogInformation("Analysed {Bytes}-byte {Kind} document: {Strategy}, {Pages} page(s), {Parts} part(s) in {ElapsedMs:F0} ms",
            file.Length, classification.Kind, prepared.Strategy, classification.PageCount, prepared.Parts.Count,
            run.Elapsed.TotalMilliseconds);
        metrics.RecordDocument(classification.Kind.ToString(), prepared.Strategy.ToString(), StatusCodes.Status200OK);

        var parts = prepared.Parts
            .Select(p => new PreparedPartDto(p.Role, p.MediaType, p.Page, p.Source, p.Text, includeData == false ? null : p.Data))
            .ToList();
        return Results.Ok(new AnalyzeDocumentResponse(file.FileName, classification, parts, agent, run.Risks));
    }
    catch (Exception ex) when (ProblemMapping.Handles(ex))
    {
        return problems.ToResult(ex, file.Length, run.Classification, httpContext);
    }
})
.DisableAntiforgery()
.Accepts<IFormFile>("multipart/form-data")
.WithName("AnalyzeDocument")
.WithSummary("Classify an uploaded PDF, Excel, Word or image document, prepare it for an AI agent and route it")
.WithDescription("The preparation strategy (page images, markdown, page images with OCR text, structured data) is chosen per page "
    + "from the content and reported in classification.strategy and classification.pages[].strategy. "
    + "includeData=false leaves page images out of the response (parts keep their metadata and text).")
.Produces<AnalyzeDocumentResponse>()
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status413PayloadTooLarge)
.ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
.ProducesProblem(StatusCodes.Status422UnprocessableEntity)
.ProducesProblem(StatusCodes.Status502BadGateway)
.ProducesProblem(StatusCodes.Status503ServiceUnavailable)
.ProducesProblem(StatusCodes.Status504GatewayTimeout)
.WithMetadata(new RequestSizeLimitAttribute(maxBodyBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxBodyBytes })
.WithRequestTimeout(TimeSpan.FromSeconds(documentOptions.RequestTimeoutSeconds))
.RequireRateLimiting(DocumentsPolicy);

app.MapPost("/api/documents/extract", async (
    IFormFile? file,
    DocumentPipeline pipeline,
    ProblemMapping problems,
    IDocumentExtractor extractor,
    IOptions<DocumentGatewayOptions> options,
    DocumentMetrics metrics,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    if (!extractor.IsConfigured)
        return Problem(StatusCodes.Status503ServiceUnavailable, "extraction is not configured");
    if (file is null || file.Length == 0)
        return Problem(StatusCodes.Status400BadRequest, "file is required");
    if (file.Length > options.Value.MaxFileBytes)
        return Problem(StatusCodes.Status413PayloadTooLarge, $"file must be at most {options.Value.MaxFileBytes} bytes");

    var run = pipeline.NewRun();
    Telemetry.TagRequest(httpContext, "intake.file.bytes", file.Length);
    metrics.RecordQueueWait("extract", Telemetry.RequestAge(httpContext));
    try
    {
        await pipeline.PrepareAsync(file, run, ct);
        TagDocument(httpContext, run.Classification!, run.Prepared!);
        // Who asked and what was uploaded, so each model call lands in the usage history (the key as a fingerprint only).
        var usageContext = new UsageContext(httpContext.GetCaller(), UsageFileName.Clean(file.FileName), file.Length,
            run.Classification!.Kind.ToString(), run.Classification.PageCount, Activity.Current?.TraceId.ToString());
        var response = await extractor.ExtractAsync(file.FileName, run.Classification!, run.Prepared!, ct, usageContext);
        // Findings about the file (structure and text) and about the answer, in one list.
        response = response with { Risks = [.. run.Risks, .. response.Risks ?? []] };
        run.Lap("extract");
        Telemetry.TagRequest(httpContext, "intake.outcome", "ok");
        Telemetry.TagRequest(httpContext, "intake.extraction.document_type", response.DocumentType.ToString());
        metrics.RecordDocument(run.Classification!.Kind.ToString(), run.Prepared!.Strategy.ToString(), StatusCodes.Status200OK);
        return Results.Ok(response);
    }
    catch (Exception ex) when (ProblemMapping.Handles(ex))
    {
        return problems.ToResult(ex, file.Length, run.Classification, httpContext);
    }
})
.DisableAntiforgery()
.RequireApiKey()
.WithName("ExtractDocument")
.WithSummary("Extract borrower, lender, collateral and UCC details from a lending document")
.WithDescription("Reads a loan application, UCC-1, UCC-3 or loan and security agreement with an OpenAI model (page images when the "
    + "document is scanned or laid out, text otherwise). Returns the parties and the document's fields, each with its page and a short quote; "
    + "every box on the UCC-1 and UCC-3 forms is its own attribute, checkboxes are true, false or null when they cannot be told, "
    + "and anything else printed on the document comes back in otherFields as name/value pairs. The document's pages leave this service "
    + "and go to the model provider.")
.Produces<ExtractDocumentResponse>()
.ProducesProblem(StatusCodes.Status401Unauthorized)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status413PayloadTooLarge)
.ProducesProblem(StatusCodes.Status422UnprocessableEntity)
.ProducesProblem(StatusCodes.Status502BadGateway)
.ProducesProblem(StatusCodes.Status503ServiceUnavailable)
.ProducesProblem(StatusCodes.Status504GatewayTimeout)
.WithMetadata(new RequestSizeLimitAttribute(maxBodyBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxBodyBytes })
.WithRequestTimeout(TimeSpan.FromSeconds(documentOptions.RequestTimeoutSeconds))
.RequireRateLimiting(ExtractionPolicy);

app.MapUsageEndpoints();

// One line that says how this instance was configured, so a trace can be read against it. No secrets.
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation(
    "DocIntake API {Version} started in {Environment}: renderer {Renderer}, extraction {Extraction} (vision {Vision}, text {Text}), "
    + "{Analyses} analyses and {Extractions} extractions at once, telemetry export {Telemetry}",
    IntakeTelemetry.Version, app.Environment.EnvironmentName,
    documentOptions.DocumentServiceUrl,
    app.Services.GetRequiredService<ExtractionClients>().IsConfigured ? "configured" : "not configured",
    extractionOptions.VisionModel, extractionOptions.TextModel,
    documentOptions.EffectiveMaxConcurrentAnalyses, extractionOptions.MaxConcurrentExtractions,
    string.IsNullOrWhiteSpace(app.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]) ? "off" : "on"));

app.Run();

public partial class Program;

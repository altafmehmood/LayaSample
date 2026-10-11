using System.Diagnostics;
using System.Threading.RateLimiting;
using ElBruno.LocalLLMs.Decisions;
using LayaSample.Api.Models;
using LayaSample.Api.Services;
using LayaSample.Api.Services.Documents;
using LayaSample.Rendering;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLocalDecisions(o =>
{
    builder.Configuration.GetSection("Laya").Bind(o);
    // Resolve a relative cache path against the project folder, not the working directory.
    if (!string.IsNullOrEmpty(o.CacheDirectory))
        o.CacheDirectory = Path.GetFullPath(o.CacheDirectory, builder.Environment.ContentRootPath);
});
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
    // Behind a gateway the request URL is not the public one; OpenApi:ServerUrl says what clients (Scalar) should call.
    var serverUrl = builder.Configuration["OpenApi:ServerUrl"];
    if (!string.IsNullOrWhiteSpace(serverUrl))
        document.Servers = [new() { Url = serverUrl }];
    return Task.CompletedTask;
}));
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IDissatisfactionAnalyzer, DissatisfactionAnalyzer>();
builder.Services.AddOptions<DocumentAnalysisOptions>()
    .Bind(builder.Configuration.GetSection(DocumentAnalysisOptions.Section))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<IDocumentClassifier, DocumentClassifier>();
builder.Services.AddSingleton<IDocumentPreparer, DocumentPreparer>();
builder.Services.AddSingleton<IDocumentRouter, DocumentRouter>();
builder.Services.AddSingleton<DocumentMetrics>();
foreach (var route in DocumentRoutes.All)
    builder.Services.AddSingleton<IDocumentAgent>(new StubDocumentAgent(route));
builder.Services.AddSingleton<ModelWarmup>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelWarmup>());
var healthChecks = builder.Services.AddHealthChecks().AddCheck<LayaHealthCheck>("laya", tags: [HealthReporting.Ready]);

// Endpoint limits and where rendering runs are fixed when the app is built, so the options are read here;
// ValidateOnStart still checks them (and refuses to start with neither a renderer nor in-process rendering allowed).
var documentOptions = builder.Configuration.GetSection(DocumentAnalysisOptions.Section).Get<DocumentAnalysisOptions>() ?? new();
if (!string.IsNullOrWhiteSpace(documentOptions.RendererUrl))
{
    // Untrusted bytes reach PDFium, ImageMagick and OCR only inside the isolated renderer service.
    builder.Services.AddHttpClient(RemotePageRasterizer.HttpClientName, c =>
    {
        c.BaseAddress = new Uri(documentOptions.RendererUrl.TrimEnd('/') + "/");
        // Bounded by the request's own token instead, which carries the request timeout.
        c.Timeout = Timeout.InfiniteTimeSpan;
    });
    builder.Services.AddSingleton<RemotePageRasterizer>();
    builder.Services.AddSingleton<IPageRasterizer>(sp => sp.GetRequiredService<RemotePageRasterizer>());
    healthChecks.AddCheck<RendererHealthCheck>("renderer", tags: [HealthReporting.Ready], timeout: TimeSpan.FromSeconds(5));
}
else
{
    // Development and tests only: AllowInProcessRendering must be set (see DocumentAnalysisOptions.Validate).
    builder.Services.AddOptions<RenderingOptions>()
        .Bind(builder.Configuration.GetSection(RenderingOptions.Section))
        .ValidateDataAnnotations()
        .ValidateOnStart();
    builder.Services.AddSingleton<IOcrEngine, RapidOcrEngine>();
    builder.Services.AddSingleton<IPageRasterizer, LocalPageRasterizer>();
    builder.Services.AddSingleton<OcrWarmup>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OcrWarmup>());
    healthChecks.AddCheck<OcrHealthCheck>("ocr", tags: [HealthReporting.Ready]);
}

const string DocumentsPolicy = "documents";
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
    o.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
    o.OnRejected = (context, ct) =>
    {
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
// Timeouts run first so time spent queued for an analysis slot counts against the request.
app.UseRequestTimeouts();
app.UseRateLimiter();

app.MapOpenApi();            // /openapi/v1.json
app.MapScalarApiReference(); // /scalar/v1

const int MaxTextLength = 4000;

static IResult Problem(int status, string detail) => Results.Problem(detail: detail, statusCode: status);

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
    IDocumentClassifier classifier,
    IDocumentPreparer preparer,
    IDocumentRouter router,
    IOptions<DocumentAnalysisOptions> options,
    DocumentMetrics metrics,
    ILogger<Program> logger,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    if (file is null || file.Length == 0)
        return Problem(StatusCodes.Status400BadRequest, "file is required");
    if (file.Length > options.Value.MaxFileBytes)
        return Problem(StatusCodes.Status413PayloadTooLarge, $"file must be at most {options.Value.MaxFileBytes} bytes");

    DocumentClassification? classification = null;
    try
    {
        // Buffered once: PDF, ZIP and TIFF need random access. The buffer is sized exactly so classify and prepare
        // share it rather than each taking a copy.
        await using var stream = new MemoryStream((int)file.Length);
        await file.CopyToAsync(stream, ct);

        var started = Stopwatch.GetTimestamp();
        var stage = started;
        TimeSpan Lap(string name)
        {
            var elapsed = Stopwatch.GetElapsedTime(stage);
            metrics.RecordStage(name, elapsed);
            stage = Stopwatch.GetTimestamp();
            return elapsed;
        }

        classification = await classifier.ClassifyAsync(stream, ct);
        Lap("classify");
        var prepared = await preparer.PrepareAsync(stream, classification, ct);
        Lap("prepare");
        var agent = dispatch == false ? null : await router.RouteAsync(classification, prepared, ct);
        Lap("route");

        // Content only, never file names or text: both can carry personal data.
        logger.LogInformation("Analysed {Bytes}-byte {Kind} document: {Strategy}, {Pages} page(s), {Parts} part(s) in {ElapsedMs:F0} ms",
            file.Length, classification.Kind, prepared.Strategy, classification.PageCount, prepared.Parts.Count,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        metrics.RecordDocument(classification.Kind.ToString(), prepared.Strategy.ToString(), StatusCodes.Status200OK);

        var parts = prepared.Parts
            .Select(p => new PreparedPartDto(p.Role, p.MediaType, p.Page, p.Source, p.Text, includeData == false ? null : p.Data))
            .ToList();
        return Results.Ok(new AnalyzeDocumentResponse(file.FileName, classification, parts, agent));
    }
    catch (DocumentException ex)
    {
        // The inner exception says why a library rejected the content; the client only gets the summary.
        logger.LogInformation(ex.InnerException, "Rejected {Bytes}-byte {Kind} document with {Status}: {Reason}",
            file.Length, classification?.Kind.ToString() ?? "unclassified", ex.StatusCode, ex.Message);
        metrics.RecordDocument(classification?.Kind.ToString() ?? "unclassified", "none", ex.StatusCode);
        return Problem(ex.StatusCode, ex.Message);
    }
    catch (OcrUnavailableException ex)
    {
        logger.LogError(ex, "OCR is unavailable");
        metrics.RecordDocument(classification?.Kind.ToString() ?? "unclassified", "none", StatusCodes.Status503ServiceUnavailable);
        return Problem(StatusCodes.Status503ServiceUnavailable, "OCR is unavailable; retry later");
    }
    catch (RendererUnavailableException ex)
    {
        logger.LogWarning(ex, "Renderer is unavailable");
        metrics.RecordDocument(classification?.Kind.ToString() ?? "unclassified", "none", StatusCodes.Status503ServiceUnavailable);
        httpContext.Response.Headers.RetryAfter = "10";
        return Problem(StatusCodes.Status503ServiceUnavailable, "document rendering is unavailable; retry later");
    }
    catch (RenderingFailedException ex)
    {
        // Most likely this document crashed the renderer; retrying it would crash it again.
        logger.LogError(ex, "Renderer failed on a {Bytes}-byte {Kind} document", file.Length, classification?.Kind.ToString() ?? "unclassified");
        metrics.RecordDocument(classification?.Kind.ToString() ?? "unclassified", "none", StatusCodes.Status502BadGateway);
        return Problem(StatusCodes.Status502BadGateway, "document could not be rendered");
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

app.Run();

public partial class Program;

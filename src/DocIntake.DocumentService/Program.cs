using System.Text.Json;
using System.Threading.RateLimiting;
using DocIntake.Contracts;
using DocIntake.Rendering;
using DocIntake.ServiceDefaults;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DocIntake.DocumentService;

/// <summary>
/// The document service: the only process that reads untrusted document bytes. It classifies an upload and prepares it for
/// an agent (page images, OCR text, markdown, form data), using PdfPig, the Office libraries, PDFium, ImageMagick and OCR.
/// Deploy it without network egress, with a read-only file system and its own memory/CPU limits (see docker-compose.yml).
/// If a document crashes it, only the requests in flight here fail; the gateway stays up.
/// </summary>
/// <remarks>A named entry point rather than top-level statements, so tests can host it next to the gateway's Program.</remarks>
public sealed class DocumentServiceEntryPoint
{
    private const string JobsPolicy = "jobs";

    public static void Main(string[] args) => Build(args).Run();

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddIntakeTelemetry("docintake-document", RenderingMetrics.MeterName);

        builder.Services.AddProblemDetails();
        builder.Services.AddOptions<RenderingOptions>()
            .Bind(builder.Configuration.GetSection(RenderingOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddOptions<DocumentAnalysisOptions>()
            .Bind(builder.Configuration.GetSection(DocumentAnalysisOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton<IOcrEngine, RapidOcrEngine>();
        builder.Services.AddSingleton<IPageRasterizer, LocalPageRasterizer>();
        builder.Services.AddSingleton<IDocumentClassifier, DocumentClassifier>();
        builder.Services.AddSingleton<IDocumentPreparer, DocumentPreparer>();
        builder.Services.AddSingleton<OcrWarmup>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OcrWarmup>());
        builder.Services.AddHealthChecks().AddCheck<OcrHealthCheck>("ocr", tags: [HealthReporting.Ready]);

        // Limits are fixed when the pipeline is built, so they are read here; ValidateOnStart still checks them.
        var options = builder.Configuration.GetSection(RenderingOptions.Section).Get<RenderingOptions>() ?? new();
        var maxBodyBytes = options.MaxDocumentBytes + 1024 * 1024;
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxBodyBytes);
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxBodyBytes);
        builder.Services.AddRateLimiter(o =>
        {
            o.AddConcurrencyLimiter(JobsPolicy, l =>
            {
                l.PermitLimit = options.EffectiveMaxConcurrentJobs;
                l.QueueLimit = options.MaxQueuedJobs;
                l.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
            o.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
            o.OnRejected = (context, _) =>
            {
                RenderingMetrics.Rejections.Add(1);
                System.Diagnostics.Activity.Current?.SetTag("intake.rejected", "rate_limit");
                context.HttpContext.RequestServices.GetRequiredService<ILogger<DocumentServiceEntryPoint>>()
                    .RateLimited();
                context.HttpContext.Response.Headers.RetryAfter = "10";
                return context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = { Status = StatusCodes.Status503ServiceUnavailable, Detail = "document service is busy; retry later" }
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

        // The gateway classifies first and decides from the findings whether the document goes on, so a refused document is
        // never prepared (rendered, OCRed). Each call carries the file itself: the service keeps nothing between calls.
        app.MapPost("/documents/classify", async (IFormFile file, IDocumentClassifier classifier, ILogger<DocumentServiceEntryPoint> logger,
            CancellationToken ct) =>
        {
            if (file.Length > options.MaxDocumentBytes)
                return Problem(StatusCodes.Status413PayloadTooLarge, $"document must be at most {options.MaxDocumentBytes} bytes");
            try
            {
                // The form file is already on disk past 64 KB (ASP.NET spools it); read from that stream, never a copy in memory.
                await using var stream = file.OpenReadStream();
                var classification = await classifier.ClassifyAsync(stream, ct);
                logger.Classified(file.Length, classification.Kind, classification.Strategy, classification.PageCount);
                return Results.Json(new ClassifyResponse(classification, classification.Risks), IntakeJson.Options);
            }
            catch (Exception ex) when (ServiceProblems.FromException(ex) is { } problem)
            {
                Log(logger, ex, file.Length);
                return problem;
            }
        })
        .DisableAntiforgery()
        .RequireRateLimiting(JobsPolicy);

        app.MapPost("/documents/prepare", async (IFormFile file, [FromForm] string classification, IDocumentPreparer preparer,
            ILogger<DocumentServiceEntryPoint> logger, CancellationToken ct) =>
        {
            if (file.Length > options.MaxDocumentBytes)
                return Problem(StatusCodes.Status413PayloadTooLarge, $"document must be at most {options.MaxDocumentBytes} bytes");
            DocumentClassification? classified;
            try
            {
                classified = JsonSerializer.Deserialize<DocumentClassification>(classification, IntakeJson.Options);
            }
            catch (JsonException)
            {
                return Problem(StatusCodes.Status400BadRequest, "classification is not valid JSON");
            }
            if (classified is null)
                return Problem(StatusCodes.Status400BadRequest, "classification is required");

            try
            {
                await using var stream = file.OpenReadStream();
                var prepared = await preparer.PrepareAsync(stream, classified, ct);
                logger.Prepared(file.Length, classified.Kind, prepared.Strategy, prepared.Parts.Count);
                // Findings about the text travel next to the document; the gateway decides what they mean.
                return Results.Json(new PrepareResponse(prepared, TextRisks.Find(prepared)), IntakeJson.Options);
            }
            catch (Exception ex) when (ServiceProblems.FromException(ex) is { } problem)
            {
                Log(logger, ex, file.Length);
                return problem;
            }
        })
        .DisableAntiforgery()
        .RequireRateLimiting(JobsPolicy);

        return app;
    }

    /// <summary>A bad file is routine (Information, with the library's reason); a broken service is not.</summary>
    private static void Log(ILogger logger, Exception ex, long bytes)
    {
        if (ex is IntakeFailureException f)
            logger.UploadRefused(f.InnerException, bytes, f.Kind, f.Message);
        else
            logger.UploadFailed(ex, bytes);
    }

    private static IResult Problem(int status, string detail) => Results.Problem(detail: detail, statusCode: status);
}

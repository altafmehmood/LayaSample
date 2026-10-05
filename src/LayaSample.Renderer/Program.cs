using System.Text.Json;
using System.Threading.RateLimiting;
using LayaSample.Rendering;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LayaSample.Renderer;

/// <summary>
/// The renderer service: the only process that hands untrusted document bytes to PDFium, ImageMagick and OCR.
/// Deploy it without network egress, with a read-only file system and its own memory/CPU limits (see
/// docker-compose.yml). If a document crashes it, only the requests in flight here fail; the API stays up.
/// </summary>
/// <remarks>A named entry point rather than top-level statements, so tests can host it next to the API's Program.</remarks>
public sealed class RendererEntryPoint
{
    private const string JobsPolicy = "jobs";

    public static void Main(string[] args) => Build(args).Run();

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddProblemDetails();
        builder.Services.AddOptions<RenderingOptions>()
            .Bind(builder.Configuration.GetSection(RenderingOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton<IOcrEngine, RapidOcrEngine>();
        builder.Services.AddSingleton<IPageRasterizer, LocalPageRasterizer>();
        builder.Services.AddSingleton<OcrWarmup>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OcrWarmup>());
        builder.Services.AddHealthChecks().AddCheck<OcrHealthCheck>("ocr", tags: [HealthReporting.Ready]);

        // Limits are fixed when the pipeline is built, so they are read here; ValidateOnStart still checks them.
        var options = builder.Configuration.GetSection(RenderingOptions.Section).Get<RenderingOptions>() ?? new();
        var maxBodyBytes = options.MaxDocumentBytes + 64 * 1024;
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
                context.HttpContext.Response.Headers.RetryAfter = "10";
                return context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = { Status = StatusCodes.Status503ServiceUnavailable, Detail = "renderer is busy; retry later" }
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

        app.MapPost("/inspect", async (HttpRequest request, string mediaType, int maxFrames, IPageRasterizer rasterizer, CancellationToken ct) =>
        {
            if (mediaType is not (MediaTypes.Tiff or MediaTypes.Png or MediaTypes.Jpeg))
                return Problem(StatusCodes.Status400BadRequest, $"{mediaType} is not an image type");
            if (maxFrames < 1)
                return Problem(StatusCodes.Status400BadRequest, "maxFrames must be at least 1");
            if (await ReadBodyAsync(request, options.MaxDocumentBytes, ct) is not { } image)
                return Problem(StatusCodes.Status413PayloadTooLarge, $"image must be at most {options.MaxDocumentBytes} bytes");

            try
            {
                return Results.Ok(await rasterizer.InspectImageAsync(image, mediaType, maxFrames, ct));
            }
            catch (InvalidDataException ex)
            {
                return Problem(StatusCodes.Status422UnprocessableEntity, ex.Message);
            }
        })
        .RequireRateLimiting(JobsPolicy);

        app.MapPost("/render", async (IFormFile document, [FromForm] string job, IPageRasterizer rasterizer, ILogger<RendererEntryPoint> logger,
            CancellationToken ct) =>
        {
            if (document.Length > options.MaxDocumentBytes)
                return Problem(StatusCodes.Status413PayloadTooLarge, $"document must be at most {options.MaxDocumentBytes} bytes");
            var (spec, error) = Validate(job, options);
            if (spec is null)
                return Problem(StatusCodes.Status400BadRequest, error!);

            var bytes = new byte[document.Length];
            await using (var stream = document.OpenReadStream())
                await stream.ReadExactlyAsync(bytes, ct);

            try
            {
                var pages = await rasterizer.RenderAsync(new RenderJob(bytes, spec.MediaType, spec.Pages), ct);
                return Results.Ok(new RenderResponse(pages));
            }
            catch (InvalidDataException ex)
            {
                // The API relays this as 422; the inner exception says which library rejected the content.
                logger.LogInformation(ex.InnerException, "Rejected {Bytes}-byte {MediaType} document: {Reason}", bytes.Length, spec.MediaType, ex.Message);
                return Problem(StatusCodes.Status422UnprocessableEntity, ex.Message);
            }
            catch (OcrUnavailableException ex)
            {
                logger.LogError(ex, "OCR is unavailable");
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, type: OcrUnavailableException.ProblemType);
            }
        })
        .DisableAntiforgery()
        .RequireRateLimiting(JobsPolicy);

        return app;
    }

    private static IResult Problem(int status, string detail) => Results.Problem(detail: detail, statusCode: status);

    private static (RenderRequest? Spec, string? Error) Validate(string json, RenderingOptions options)
    {
        RenderRequest? spec;
        try
        {
            spec = JsonSerializer.Deserialize<RenderRequest>(json, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return (null, "job is not valid JSON");
        }

        if (spec?.MediaType is null || spec.Pages is null) return (null, "job needs mediaType and pages");
        if (!MediaTypes.IsRenderable(spec.MediaType)) return (null, $"{spec.MediaType} cannot be rendered");
        if (spec.Pages.Count > options.MaxPagesPerJob) return (null, $"at most {options.MaxPagesPerJob} pages per job");
        if (spec.Pages.Any(p => p is null || p.Number < 1 || p.Dpi is < 1 or > 1200)) return (null, "page numbers start at 1 and dpi is 1-1200");
        return (spec, null);
    }

    /// <summary>The request body, or null when it is larger than <paramref name="max"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, long max, CancellationToken ct)
    {
        if (request.ContentLength > max) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

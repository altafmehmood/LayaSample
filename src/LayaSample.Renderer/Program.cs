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

        builder.AddLayaTelemetry("laya-sample-renderer");

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
                RenderingMetrics.Rejections.Add(1);
                System.Diagnostics.Activity.Current?.SetTag("laya.rejected", "rate_limit");
                context.HttpContext.RequestServices.GetRequiredService<ILogger<RendererEntryPoint>>()
                    .LogWarning("Render request rejected: every slot and queue place is taken");
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
            await using var image = await SpoolBodyAsync(request, options.MaxDocumentBytes, ct);
            if (image is null)
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

            // The form file is already on disk past 64 KB (ASP.NET spools it); render from that stream, never a copy in memory.
            await using var stream = document.OpenReadStream();

            try
            {
                var pages = await rasterizer.RenderAsync(new RenderJob(stream, spec.MediaType, spec.Pages), ct);
                logger.LogInformation("Rendered {Pages} page(s) of a {Bytes}-byte {MediaType} document ({Ocr} with OCR)",
                    pages.Count, document.Length, spec.MediaType, pages.Count(p => p.OcrText is not null));
                return Results.Ok(new RenderResponse(pages));
            }
            catch (InvalidDataException ex)
            {
                // The API relays this as 422; the inner exception says which library rejected the content.
                logger.LogInformation(ex.InnerException, "Rejected {Bytes}-byte {MediaType} document: {Reason}", document.Length, spec.MediaType, ex.Message);
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

    /// <summary>
    /// The request body spooled to a temporary file (deleted when the stream is disposed) and rewound, or null when it is
    /// larger than <paramref name="max"/>. Seekable, which the image parsers need, without holding the body in memory.
    /// </summary>
    private static async Task<Stream?> SpoolBodyAsync(HttpRequest request, long max, CancellationToken ct)
    {
        if (request.ContentLength > max) return null;
        var file = new FileStream(Path.Combine(Path.GetTempPath(), "laya-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        var chunk = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (file.Length + read > max)
                {
                    await file.DisposeAsync();
                    return null;
                }
                await file.WriteAsync(chunk.AsMemory(0, read), ct);
            }
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}

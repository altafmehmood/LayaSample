using System.Diagnostics;
using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;

namespace DocIntake.Rendering;

/// <summary>
/// Renders in the current process: inside the renderer service, or inside the API in development. Work is
/// synchronous; callers bound how many jobs run at once.
/// </summary>
public sealed class LocalPageRasterizer(IOcrEngine ocr, IOptions<RenderingOptions> options) : IPageRasterizer
{
    private const int DefaultPdfDpi = 150;

    public Task<IReadOnlyList<RasterPage>> InspectImageAsync(Stream image, string mediaType, int maxFrames, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequireSeekable(image);
        using var span = IntakeTelemetry.Source.StartActivity("renderer.inspect");
        span?.SetTag("intake.render.media_type", mediaType);
        span?.SetTag("intake.render.input_bytes", image.Length);
        try
        {
            var frames = Readable("image could not be read", () => RasterImages.Inspect(image, mediaType, maxFrames));
            span?.SetTag("intake.render.frames", frames.Count);
            return Task.FromResult(frames);
        }
        catch (Exception ex)
        {
            span.RecordFailure(ex);
            throw;
        }
    }

    public Task<IReadOnlyList<RenderedPage>> RenderAsync(RenderJob job, CancellationToken ct = default)
    {
        if (!MediaTypes.IsRenderable(job.MediaType)) throw new InvalidDataException($"{job.MediaType} cannot be rendered");
        RequireSeekable(job.Document);

        using var span = IntakeTelemetry.Source.StartActivity("renderer.render");
        span?.SetTag("intake.render.media_type", job.MediaType);
        span?.SetTag("intake.render.input_bytes", job.Document.Length);
        span?.SetTag("intake.render.pages_requested", job.Pages.Count);
        span?.SetTag("intake.render.pages_ocr", job.Pages.Count(p => p.Ocr));
        var mediaTag = new KeyValuePair<string, object?>("media_type", job.MediaType);
        var started = Stopwatch.GetTimestamp();
        RenderingMetrics.JobsInFlight.Add(1);
        var outcome = "ok";
        try
        {
            var pages = new List<RenderedPage>(job.Pages.Count);
            long outputBytes = 0;
            foreach (var page in job.Pages)
            {
                ct.ThrowIfCancellationRequested();
                using var pageSpan = IntakeTelemetry.Source.StartActivity("renderer.page");
                pageSpan?.SetTag("intake.render.page", page.Number);
                pageSpan?.SetTag("intake.render.dpi", page.Dpi ?? DefaultPdfDpi);
                pageSpan?.SetTag("intake.render.grayscale", page.Grayscale);
                var pageStarted = Stopwatch.GetTimestamp();
                using var bitmap = Readable($"page {page.Number} could not be rendered", () => job.MediaType == MediaTypes.Pdf
                    ? RenderPdfPage(job.Document, page.Number, page.Dpi ?? DefaultPdfDpi)
                    : RasterImages.Decode(job.Document, job.MediaType, page.Number - 1));
                var png = Png(bitmap, page.Grayscale);
                RenderingMetrics.PageDuration.Record(Stopwatch.GetElapsedTime(pageStarted).TotalSeconds, mediaTag);
                pageSpan?.SetTag("intake.render.width", bitmap.Width);
                pageSpan?.SetTag("intake.render.height", bitmap.Height);
                pageSpan?.SetTag("intake.render.png_bytes", png.Length);

                var text = "";
                var ranOcr = page.Ocr && options.Value.EnableOcr;
                if (ranOcr)
                {
                    using var ocrSpan = IntakeTelemetry.Source.StartActivity("renderer.ocr");
                    ocrSpan?.SetTag("intake.render.page", page.Number);
                    var ocrStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        text = ocr.Recognize(bitmap, ct);
                    }
                    catch (Exception ex)
                    {
                        ocrSpan.RecordFailure(ex);
                        throw;
                    }
                    finally
                    {
                        RenderingMetrics.OcrDuration.Record(Stopwatch.GetElapsedTime(ocrStarted).TotalSeconds);
                    }
                    ocrSpan?.SetTag("intake.ocr.characters", text.Length);
                    RenderingMetrics.OcrCharacters.Add(text.Length);
                }
                RenderingMetrics.PagesRendered.Add(1, mediaTag, new KeyValuePair<string, object?>("ocr", ranOcr));
                outputBytes += png.Length;
                pages.Add(new RenderedPage(page.Number, png, text.Length == 0 ? null : text));
            }
            span?.SetTag("intake.render.pages_rendered", pages.Count);
            span?.SetTag("intake.render.output_bytes", outputBytes);
            return Task.FromResult<IReadOnlyList<RenderedPage>>(pages);
        }
        catch (Exception ex)
        {
            outcome = ex is OperationCanceledException ? "cancelled" : ex is InvalidDataException ? "invalid" : "failed";
            span.RecordFailure(ex, outcome);
            throw;
        }
        finally
        {
            RenderingMetrics.JobsInFlight.Add(-1);
            RenderingMetrics.RenderDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, mediaTag,
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private static void RequireSeekable(Stream document)
    {
        if (!document.CanSeek)
            throw new ArgumentException("the document stream must be seekable: each page is read from its start", nameof(document));
    }

    /// <summary>The libraries surface bad input as many exception types; callers see one.</summary>
    private static T Readable<T>(string message, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not (InvalidDataException or OperationCanceledException or OcrUnavailableException))
        {
            throw new InvalidDataException(message, ex);
        }
    }

    private static SKBitmap RenderPdfPage(Stream pdf, int number, int dpi)
    {
        if (number < 1) throw new InvalidDataException($"PDF has no page {number}");
        // A page can declare any size (up to 200 inches square), so cap the bitmap rather than the DPI alone.
        // leaveOpen: the same stream serves every page, and belongs to whoever opened it.
        pdf.Position = 0;
        var size = Conversion.GetPageSize(pdf, number - 1, leaveOpen: true);
        var pixels = (double)size.Width / 72 * dpi * ((double)size.Height / 72 * dpi);
        if (pixels > RasterImages.MaxPixels)
            dpi = (int)(dpi * Math.Sqrt(RasterImages.MaxPixels / pixels));
        if (dpi < 1) throw new InvalidDataException($"PDF page {number} is too large to render");

        // Render what a person sees: WithFormFill draws AcroForm field values, WithAnnotations draws
        // Fill & Sign text, signatures and stamps. Without them a filled-in PDF renders blank.
        var render = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true);
        pdf.Position = 0;
        return Conversion.ToImage(pdf, page: number - 1, leaveOpen: true, options: render);
    }

    private static byte[] Png(SKBitmap bitmap, bool grayscale)
    {
        // Fax pages are black and white: a greyscale PNG is a fraction of the RGBA size and loses nothing.
        using var gray = grayscale ? bitmap.Copy(SKColorType.Gray8) : null;
        using var data = (gray ?? bitmap).Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

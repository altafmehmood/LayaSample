using Microsoft.Extensions.Options;
using PDFtoImage;
using SkiaSharp;

namespace LayaSample.Rendering;

/// <summary>
/// Renders in the current process: inside the renderer service, or inside the API in development. Work is
/// synchronous; callers bound how many jobs run at once.
/// </summary>
public sealed class LocalPageRasterizer(IOcrEngine ocr, IOptions<RenderingOptions> options) : IPageRasterizer
{
    private const int DefaultPdfDpi = 150;

    public Task<IReadOnlyList<RasterPage>> InspectImageAsync(byte[] image, string mediaType, int maxFrames, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Readable("image could not be read", () => RasterImages.Inspect(image, mediaType, maxFrames)));
    }

    public Task<IReadOnlyList<RenderedPage>> RenderAsync(RenderJob job, CancellationToken ct = default)
    {
        if (!MediaTypes.IsRenderable(job.MediaType)) throw new InvalidDataException($"{job.MediaType} cannot be rendered");

        var pages = new List<RenderedPage>(job.Pages.Count);
        foreach (var page in job.Pages)
        {
            ct.ThrowIfCancellationRequested();
            using var bitmap = Readable($"page {page.Number} could not be rendered", () => job.MediaType == MediaTypes.Pdf
                ? RenderPdfPage(job.Document, page.Number, page.Dpi ?? DefaultPdfDpi)
                : RasterImages.Decode(job.Document, job.MediaType, page.Number - 1));
            var png = Png(bitmap, page.Grayscale);
            var text = page.Ocr && options.Value.EnableOcr ? ocr.Recognize(bitmap, ct) : "";
            pages.Add(new RenderedPage(page.Number, png, text.Length == 0 ? null : text));
        }
        return Task.FromResult<IReadOnlyList<RenderedPage>>(pages);
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

    private static SKBitmap RenderPdfPage(byte[] pdf, int number, int dpi)
    {
        if (number < 1) throw new InvalidDataException($"PDF has no page {number}");
        // A page can declare any size (up to 200 inches square), so cap the bitmap rather than the DPI alone.
        var size = Conversion.GetPageSize(pdf, number - 1);
        var pixels = (double)size.Width / 72 * dpi * ((double)size.Height / 72 * dpi);
        if (pixels > RasterImages.MaxPixels)
            dpi = (int)(dpi * Math.Sqrt(RasterImages.MaxPixels / pixels));
        if (dpi < 1) throw new InvalidDataException($"PDF page {number} is too large to render");

        // Render what a person sees: WithFormFill draws AcroForm field values, WithAnnotations draws
        // Fill & Sign text, signatures and stamps. Without them a filled-in PDF renders blank.
        var render = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true);
        return Conversion.ToImage(new MemoryStream(pdf, writable: false), page: number - 1, options: render);
    }

    private static byte[] Png(SKBitmap bitmap, bool grayscale)
    {
        // Fax pages are black and white: a greyscale PNG is a fraction of the RGBA size and loses nothing.
        using var gray = grayscale ? bitmap.Copy(SKColorType.Gray8) : null;
        using var data = (gray ?? bitmap).Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

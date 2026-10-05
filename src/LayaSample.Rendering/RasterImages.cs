using System.Runtime.InteropServices;
using ImageMagick;
using SkiaSharp;

namespace LayaSample.Rendering;

/// <summary>Reads TIFF (including multi-page fax), PNG and JPEG via ImageMagick.</summary>
internal static class RasterImages
{
    // Guards against decompression bombs: a 300 dpi A3 scan is ~17M pixels.
    public const long MaxPixels = 60_000_000;

    private static readonly HashSet<CompressionMethod> FaxCompressions =
        [CompressionMethod.Fax, CompressionMethod.Group4, CompressionMethod.JBIG1, CompressionMethod.JBIG2];

    static RasterImages()
    {
        // Process-wide backstops inside ImageMagick's own decoders, behind the per-page check in SquarePixelSize.
        ResourceLimits.Width = 100_000;
        ResourceLimits.Height = 100_000;
        ResourceLimits.MaxMemoryRequest = 1024UL * 1024 * 1024;
    }

    /// <param name="maxFrames">Pages read at most; a TIFF can hold any number of them.</param>
    /// <exception cref="InvalidDataException">A page is larger than the decode limit.</exception>
    public static IReadOnlyList<RasterPage> Inspect(byte[] bytes, string mediaType, int maxFrames)
    {
        // Ping reads each page's header without decoding pixels.
        using var pages = new MagickImageCollection();
        var settings = Settings(mediaType);
        settings.FrameCount = (uint)maxFrames;
        pages.Ping(bytes, settings);
        if (pages.Count == 0) throw new InvalidDataException("image has no pages");
        return pages.Select((p, i) =>
        {
            var (width, height) = SquarePixelSize(p, i);
            return new RasterPage(width, height, FaxCompressions.Contains(p.Compression) || p.Depth == 1);
        }).ToList();
    }

    /// <summary>Decodes one page (zero-based) to an upright RGBA bitmap with square pixels.</summary>
    public static SKBitmap Decode(byte[] bytes, string mediaType, int pageIndex)
    {
        // Check the size from the page header before decoding its pixels. Pinging a TIFF frame by index fails, so this
        // pings the frames up to it (headers only; the frame count was capped at classification).
        var settings = Settings(mediaType);
        settings.FrameCount = (uint)pageIndex + 1;
        using (var headers = new MagickImageCollection())
        {
            headers.Ping(bytes, settings);
            if (pageIndex >= headers.Count) throw new InvalidDataException($"image has no page {pageIndex + 1}");
            SquarePixelSize(headers[pageIndex], pageIndex);
        }

        settings.FrameIndex = (uint)pageIndex;
        settings.FrameCount = 1;
        using var image = new MagickImage(bytes, settings);
        // Phone photos are often stored sideways with an EXIF orientation tag.
        image.AutoOrient();

        // Standard-mode fax is 204x98 dpi: stretch vertically so the page keeps its real proportions.
        var (width, height) = SquarePixelSize(image, pageIndex);
        if (width != image.Width || height != image.Height)
            image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });

        byte[] rgba;
        using (var pixels = image.GetPixelsUnsafe())
            rgba = pixels.ToByteArray(PixelMapping.RGBA) ?? throw new InvalidDataException("image page could not be decoded");
        return WrapPixels(rgba, width, height);
    }

    /// <summary>Lends the pixel array to Skia (pinned until the bitmap is disposed) instead of copying it.</summary>
    private static SKBitmap WrapPixels(byte[] rgba, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        if (rgba.Length != info.BytesSize) throw new InvalidDataException("image page could not be decoded");

        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        var bitmap = new SKBitmap();
        if (bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, (_, _) => handle.Free())) return bitmap;

        handle.Free();
        bitmap.Dispose();
        throw new InvalidDataException("image page could not be decoded");
    }

    /// <summary>The page size once non-square pixels are stretched, checked against the decode limit.</summary>
    private static (int Width, int Height) SquarePixelSize(IMagickImage image, int pageIndex)
    {
        double width = image.Width, height = image.Height;
        var density = image.Density;
        if (density.X > 0 && density.Y > 0 && Math.Abs(density.X - density.Y) >= 1)
            height = Math.Round(height * density.X / density.Y);

        if (width < 1 || height < 1 || width * height > MaxPixels)
            throw new InvalidDataException($"image page {pageIndex + 1} size {width}x{height} is out of range");
        return ((int)width, (int)height);
    }

    // An explicit format stops ImageMagick from sniffing (and decoding) anything other than the type we identified.
    private static MagickReadSettings Settings(string mediaType) => new()
    {
        Format = mediaType switch
        {
            MediaTypes.Tiff => MagickFormat.Tiff,
            MediaTypes.Png => MagickFormat.Png,
            MediaTypes.Jpeg => MagickFormat.Jpeg,
            _ => throw new InvalidDataException($"{mediaType} is not a supported image type")
        }
    };
}

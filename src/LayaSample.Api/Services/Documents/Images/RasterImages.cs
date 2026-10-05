using System.Runtime.InteropServices;
using ImageMagick;
using SkiaSharp;

namespace LayaSample.Api.Services.Documents.Images;

public sealed record RasterPage(int Width, int Height, bool IsFax);

/// <summary>Reads TIFF (including multi-page fax), PNG and JPEG via ImageMagick.</summary>
public static class RasterImages
{
    // Guards against decompression bombs: a 300 dpi A3 scan is ~17M pixels.
    private const long MaxPixels = 60_000_000;

    private static readonly HashSet<CompressionMethod> FaxCompressions =
        [CompressionMethod.Fax, CompressionMethod.Group4, CompressionMethod.JBIG1, CompressionMethod.JBIG2];

    /// <exception cref="InvalidDataException">A page is larger than the decode limit.</exception>
    public static IReadOnlyList<RasterPage> Inspect(byte[] bytes, string mediaType)
    {
        // Ping reads each page's header without decoding pixels.
        using var pages = new MagickImageCollection();
        pages.Ping(bytes, Settings(mediaType));
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
        var settings = Settings(mediaType);
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

        using var pixels = image.GetPixelsUnsafe();
        var rgba = pixels.ToByteArray(PixelMapping.RGBA) ?? throw new InvalidDataException("image page could not be decoded");

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        return bitmap;
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
            DocumentSniffer.Tiff => MagickFormat.Tiff,
            DocumentSniffer.Png => MagickFormat.Png,
            DocumentSniffer.Jpeg => MagickFormat.Jpeg,
            _ => throw new InvalidDataException($"{mediaType} is not a supported image type")
        }
    };
}

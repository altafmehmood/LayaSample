using System.Runtime.InteropServices;
using ImageMagick;
using SkiaSharp;

namespace LayaSample.Api.Services.Documents.Images;

public sealed record RasterPage(int Width, int Height, bool IsFax);

/// <summary>Reads TIFF (including multi-page fax) via ImageMagick, and PNG / JPEG via Skia.</summary>
public static class RasterImages
{
    // Guards against decompression bombs: a 300 dpi A3 scan is ~17M pixels.
    private const long MaxPixels = 60_000_000;

    private static readonly HashSet<CompressionMethod> FaxCompressions =
        [CompressionMethod.Fax, CompressionMethod.Group4, CompressionMethod.JBIG1, CompressionMethod.JBIG2];

    public static IReadOnlyList<RasterPage> Inspect(byte[] bytes, string mediaType)
    {
        if (mediaType != DocumentSniffer.Tiff)
        {
            using var codec = SKCodec.Create(SKData.CreateCopy(bytes)) ?? throw new InvalidDataException("image could not be decoded");
            return [new RasterPage(codec.Info.Width, codec.Info.Height, IsFax: false)];
        }

        // Ping reads each page's header without decoding pixels.
        using var pages = new MagickImageCollection();
        pages.Ping(bytes, TiffSettings());
        return pages
            .Select(p => new RasterPage((int)p.Width, (int)p.Height, FaxCompressions.Contains(p.Compression) || p.Depth == 1))
            .ToList();
    }

    /// <summary>Decodes one page (zero-based) to an RGBA bitmap with square pixels.</summary>
    public static SKBitmap Decode(byte[] bytes, string mediaType, int pageIndex)
    {
        if (mediaType != DocumentSniffer.Tiff)
            return SKBitmap.Decode(bytes) ?? throw new InvalidDataException("image could not be decoded");

        using (var headers = new MagickImageCollection())
        {
            headers.Ping(bytes, TiffSettings());
            if (pageIndex >= headers.Count) throw new InvalidDataException($"TIFF has no page {pageIndex + 1}");
            var header = headers[pageIndex];
            if ((long)header.Width * header.Height > MaxPixels)
                throw new InvalidDataException($"TIFF page size {header.Width}x{header.Height} is out of range");
        }

        var settings = TiffSettings();
        settings.FrameIndex = (uint)pageIndex;
        settings.FrameCount = 1;
        using var image = new MagickImage(bytes, settings);

        // Standard-mode fax is 204x98 dpi: stretch vertically so the page keeps its real proportions.
        var density = image.Density;
        if (density.X > 0 && density.Y > 0 && Math.Abs(density.X - density.Y) >= 1)
            image.Resize(new MagickGeometry(image.Width, (uint)Math.Round(image.Height * density.X / density.Y)) { IgnoreAspectRatio = true });

        int width = (int)image.Width, height = (int)image.Height;
        using var pixels = image.GetPixelsUnsafe();
        var rgba = pixels.ToByteArray(PixelMapping.RGBA) ?? throw new InvalidDataException("TIFF page could not be decoded");

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        return bitmap;
    }

    // An explicit format stops ImageMagick from sniffing (and decoding) anything other than the TIFF we identified.
    private static MagickReadSettings TiffSettings() => new() { Format = MagickFormat.Tiff };
}

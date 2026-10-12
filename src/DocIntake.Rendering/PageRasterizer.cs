using DocIntake.Contracts;
namespace DocIntake.Rendering;

/// <summary>
/// Renders and reads document pages. Implementations are the only code that hands untrusted document bytes to
/// native parsers (PDFium, ImageMagick) and OCR. It runs inside the document service, the only process that reads
/// untrusted document bytes.
/// </summary>
public interface IPageRasterizer
{
    /// <summary>Page sizes and fax encoding of a TIFF/PNG/JPEG, read from headers without decoding pixels.</summary>
    /// <param name="image">Seekable. Read from its start and left open; the position afterwards is unspecified.</param>
    /// <param name="maxFrames">Pages read at most; a TIFF can hold any number of them.</param>
    /// <exception cref="InvalidDataException">The image is unreadable or a page is too large.</exception>
    Task<IReadOnlyList<RasterPage>> InspectImageAsync(Stream image, string mediaType, int maxFrames, CancellationToken ct = default);

    /// <summary>Renders (PDF) or decodes (image) the requested pages to PNG, with OCR text where asked.</summary>
    /// <exception cref="InvalidDataException">The document or a page cannot be rendered.</exception>
    /// <exception cref="OcrUnavailableException"/>
    Task<IReadOnlyList<RenderedPage>> RenderAsync(RenderJob job, CancellationToken ct = default);
}

public sealed record RasterPage(int Width, int Height, bool IsFax);

/// <param name="Number">One-based page (PDF) or frame (TIFF) number.</param>
/// <param name="Dpi">PDF render resolution; ignored for images, which keep their own.</param>
/// <param name="Grayscale">Encode the PNG as greyscale (fax pages are black and white).</param>
/// <param name="Ocr">Also recognise the page's text.</param>
public sealed record PageRequest(int Number, int? Dpi = null, bool Grayscale = false, bool Ocr = false);

/// <param name="Document">
/// Seekable, read from its start (once per page for a local render) and left open. It is the upload's own stream, so the
/// document is never copied into memory just to be handed on.
/// </param>
public sealed record RenderJob(Stream Document, string MediaType, IReadOnlyList<PageRequest> Pages)
{
    /// <summary>For documents that are already in memory (embedded attachments, tests).</summary>
    public RenderJob(byte[] document, string mediaType, IReadOnlyList<PageRequest> pages)
        : this(new MemoryStream(document, writable: false), mediaType, pages) { }
}

public static class PageRasterizerExtensions
{
    /// <summary>For images that are already in memory.</summary>
    public static Task<IReadOnlyList<RasterPage>> InspectImageAsync(this IPageRasterizer rasterizer, byte[] image, string mediaType, int maxFrames,
        CancellationToken ct = default) =>
        rasterizer.InspectImageAsync(new MemoryStream(image, writable: false), mediaType, maxFrames, ct);
}

/// <param name="OcrText">Null when OCR was not asked for, is disabled, or found nothing.</param>
public sealed record RenderedPage(int Number, byte[] Png, string? OcrText);

public static class MediaTypes
{
    public const string Pdf = "application/pdf";
    public const string Tiff = "image/tiff";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    public static bool IsRenderable(string mediaType) => mediaType is Pdf or Tiff or Png or Jpeg;
}

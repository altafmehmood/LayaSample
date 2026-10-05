namespace LayaSample.Rendering;

/// <summary>
/// Renders and reads document pages. Implementations are the only code that hands untrusted document bytes to
/// native parsers (PDFium, ImageMagick) and OCR: <see cref="LocalPageRasterizer"/> in-process,
/// <see cref="RemotePageRasterizer"/> in the isolated renderer service.
/// </summary>
public interface IPageRasterizer
{
    /// <summary>Page sizes and fax encoding of a TIFF/PNG/JPEG, read from headers without decoding pixels.</summary>
    /// <param name="maxFrames">Pages read at most; a TIFF can hold any number of them.</param>
    /// <exception cref="InvalidDataException">The image is unreadable or a page is too large.</exception>
    /// <exception cref="RendererUnavailableException"/>
    /// <exception cref="RenderingFailedException"/>
    Task<IReadOnlyList<RasterPage>> InspectImageAsync(byte[] image, string mediaType, int maxFrames, CancellationToken ct = default);

    /// <summary>Renders (PDF) or decodes (image) the requested pages to PNG, with OCR text where asked.</summary>
    /// <exception cref="InvalidDataException">The document or a page cannot be rendered.</exception>
    /// <exception cref="OcrUnavailableException"/>
    /// <exception cref="RendererUnavailableException"/>
    /// <exception cref="RenderingFailedException"/>
    Task<IReadOnlyList<RenderedPage>> RenderAsync(RenderJob job, CancellationToken ct = default);
}

public sealed record RasterPage(int Width, int Height, bool IsFax);

/// <param name="Number">One-based page (PDF) or frame (TIFF) number.</param>
/// <param name="Dpi">PDF render resolution; ignored for images, which keep their own.</param>
/// <param name="Grayscale">Encode the PNG as greyscale (fax pages are black and white).</param>
/// <param name="Ocr">Also recognise the page's text.</param>
public sealed record PageRequest(int Number, int? Dpi = null, bool Grayscale = false, bool Ocr = false);

public sealed record RenderJob(byte[] Document, string MediaType, IReadOnlyList<PageRequest> Pages);

/// <param name="OcrText">Null when OCR was not asked for, is disabled, or found nothing.</param>
public sealed record RenderedPage(int Number, byte[] Png, string? OcrText);

/// <summary>The <c>job</c> part of a renderer <c>/render</c> request (the document travels as its own part).</summary>
public sealed record RenderRequest(string MediaType, IReadOnlyList<PageRequest> Pages);

public sealed record RenderResponse(IReadOnlyList<RenderedPage> Pages);

public static class MediaTypes
{
    public const string Pdf = "application/pdf";
    public const string Tiff = "image/tiff";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    public static bool IsRenderable(string mediaType) => mediaType is Pdf or Tiff or Png or Jpeg;
}

/// <summary>The OCR engine is misconfigured (a server problem, not a problem with the document).</summary>
public sealed class OcrUnavailableException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Problem type the renderer service uses for this error.</summary>
    public const string ProblemType = "urn:laya-sample:ocr-unavailable";
}

/// <summary>The renderer service cannot be reached or is saturated; retrying later may succeed.</summary>
public sealed class RendererUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The renderer failed while handling this document (it crashed, dropped the connection or returned a server error).
/// Not retried: a document that crashes the renderer would crash it again.
/// </summary>
public sealed class RenderingFailedException(string message, Exception? inner = null) : Exception(message, inner);

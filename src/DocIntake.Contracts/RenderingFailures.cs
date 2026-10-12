namespace DocIntake.Contracts;

/// <summary>The OCR engine is misconfigured (a server problem, not a problem with the document).</summary>
public sealed class OcrUnavailableException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Problem type the document service uses for this error.</summary>
    public const string ProblemType = "urn:docintake:ocr-unavailable";
}

/// <summary>The document service cannot be reached or is saturated; retrying later may succeed.</summary>
public sealed class RendererUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The document service failed while handling this document (it crashed, dropped the connection or returned a server
/// error). Not retried: a document that crashes the service would crash it again.
/// </summary>
public sealed class RenderingFailedException(string message, Exception? inner = null) : Exception(message, inner);

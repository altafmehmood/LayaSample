namespace DocIntake.Contracts;

public interface IDocumentClassifier
{
    /// <summary>Classifies the document from its content (not its name or client-supplied content type).</summary>
    /// <exception cref="IntakeFailureException">Unsupported, unreadable, encrypted or over a limit.</exception>
    /// <exception cref="RendererUnavailableException">The document service is unreachable or saturated.</exception>
    /// <exception cref="RenderingFailedException">The document service failed while handling the document.</exception>
    Task<DocumentClassification> ClassifyAsync(Stream stream, CancellationToken ct = default);
}

public interface IDocumentPreparer
{
    /// <summary>
    /// Converts the document into the form the downstream agent should receive, using the strategy the classification
    /// chose (per page for PDFs).
    /// </summary>
    /// <exception cref="IntakeFailureException">The content is unreadable.</exception>
    /// <exception cref="OcrUnavailableException"/>
    /// <exception cref="RendererUnavailableException"/>
    /// <exception cref="RenderingFailedException"/>
    Task<PreparedDocument> PrepareAsync(Stream stream, DocumentClassification classification, CancellationToken ct = default);
}

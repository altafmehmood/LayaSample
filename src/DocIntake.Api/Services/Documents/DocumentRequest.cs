namespace DocIntake.Api.Services.Documents;

/// <summary>
/// What the failure handler needs to know about a document request that has started: how big the upload was, and the run, whose
/// classification (once known) names the kind of document in the log and the metric. The upload filter puts it on the request,
/// so the endpoints do not catch exceptions to say it.
/// </summary>
public sealed record DocumentRequest(long Bytes, DocumentRun Run)
{
    public const string ItemKey = "intake.document.request";
}

public static class DocumentRequestExtensions
{
    public static DocumentRequest GetDocumentRequest(this HttpContext context) =>
        context.Items.TryGetValue(DocumentRequest.ItemKey, out var request) && request is DocumentRequest known
            ? known
            : throw new InvalidOperationException("The upload filter has not run for this request.");

    public static DocumentRequest? TryGetDocumentRequest(this HttpContext context) =>
        context.Items.TryGetValue(DocumentRequest.ItemKey, out var request) ? request as DocumentRequest : null;
}

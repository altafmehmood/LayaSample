using Microsoft.AspNetCore.Diagnostics;
using System.Diagnostics;
using DocIntake.Contracts;
using DocIntake.ServiceDefaults;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Security;

namespace DocIntake.Api.Services;

/// <summary>
/// The one place a failure becomes an HTTP answer: the status, the Retry-After header, the outcome on the request span,
/// the log level and the document metric. Everything below the endpoints throws <see cref="IntakeFailureException"/> (or
/// one of the rendering and scanner exceptions) and knows nothing of status codes.
/// </summary>
public sealed class ProblemMapping(ILogger<ProblemMapping> logger, DocumentMetrics metrics) : IExceptionHandler
{
    /// <summary>
    /// The app's exception handler: endpoints and filters just throw. A failure of an upload that has started says how big it
    /// was and what it turned out to be (the document metric, the kind in the log); any other failure (feedback, the
    /// extraction pre-check) is answered without them. Anything this does not know is left to the default handler.
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!Handles(exception))
            return false;
        var result = httpContext.TryGetDocumentRequest() is { } request
            ? ToResult(exception, request.Bytes, request.Run.Classification, httpContext)
            : ToServiceResult(exception, httpContext);
        await result.ExecuteAsync(httpContext);
        return true;
    }

    public static bool Handles(Exception ex) =>
        ex is IntakeFailureException or OcrUnavailableException or RendererUnavailableException or RenderingFailedException
            or MalwareScannerUnavailableException;

    public static IResult Problem(int status, string detail) => Results.Problem(detail: detail, statusCode: status);

    public static int StatusOf(FailureKind kind) => ServiceProblems.StatusOf(kind);

    /// <summary>What the request span's <c>intake.outcome</c> says. "rejected" means our own rules refused the upload.</summary>
    private static string OutcomeOf(FailureKind kind) => kind switch
    {
        FailureKind.Unsupported or FailureKind.Unreadable or FailureKind.OverLimit or FailureKind.Malicious
            or FailureKind.PolicyRejected => "rejected",
        FailureKind.Misconfigured => "provider_misconfigured",
        _ => "extraction_failed"
    };

    /// <summary>Caused by the upload: Information. A provider that is busy or slow: Warning. Our deployment: Error.</summary>
    private static LogLevel LevelOf(FailureKind kind) => kind switch
    {
        FailureKind.Unsupported or FailureKind.Unreadable or FailureKind.OverLimit or FailureKind.Malicious
            or FailureKind.PolicyRejected => LogLevel.Information,
        FailureKind.Misconfigured => LogLevel.Error,
        _ => LogLevel.Warning
    };

    /// <summary>
    /// Whether the inner exception may be logged. For a bad file it says why a library rejected it, which is the point of
    /// the log. For the model provider it can echo parts of the API key or of the document, so only its type is logged.
    /// </summary>
    private static bool LogsInner(FailureKind kind) => kind is FailureKind.Unsupported or FailureKind.Unreadable
        or FailureKind.OverLimit or FailureKind.Malicious or FailureKind.PolicyRejected;

    private sealed record Described(int Status, string Outcome, string Detail, int? RetryAfterSeconds, LogLevel Level, Exception? Logged,
        string Hint = "");

    private static Described Describe(Exception ex) => ex switch
    {
        IntakeFailureException f => new(StatusOf(f.Kind), OutcomeOf(f.Kind), f.Message, f.RetryAfterSeconds, LevelOf(f.Kind),
            LogsInner(f.Kind) ? f.InnerException : null,
            f.Kind == FailureKind.Misconfigured ? "; the model provider rejected our credentials, check the API key" : ""),
        OcrUnavailableException => new(StatusCodes.Status503ServiceUnavailable, "ocr_unavailable", "OCR is unavailable; retry later",
            null, LogLevel.Error, ex),
        MalwareScannerUnavailableException => new(StatusCodes.Status503ServiceUnavailable, "scanner_unavailable",
            "document scanning is unavailable; retry later", 10, LogLevel.Warning, ex),
        RendererUnavailableException => new(StatusCodes.Status503ServiceUnavailable, "renderer_unavailable",
            "document processing is unavailable; retry later", 10, LogLevel.Warning, ex),
        // Most likely this document crashed the renderer; retrying it would crash it again.
        _ => new(StatusCodes.Status502BadGateway, "render_failed", "document could not be processed", null, LogLevel.Error, ex)
    };

    /// <summary>
    /// The answer for a failure that is not about an uploaded document (feedback analysis): the same status, retry hint,
    /// outcome on the span and log line, without the document metric or a document kind.
    /// </summary>
    public IResult ToServiceResult(Exception ex, HttpContext httpContext)
    {
        var failure = Describe(ex);
        // "extraction_failed" is what a failed model call is called; the same kinds also describe the decisions service.
        // The exception middleware clears the request's endpoint before it calls a handler; the feature keeps the original.
        var endpoint = httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint ?? httpContext.GetEndpoint();
        if (failure.Outcome == "extraction_failed" && endpoint?.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName == "AnalyzeFeedback")
            failure = failure with { Outcome = "feedback_failed" };
        var cause = ex.InnerException?.GetType().Name ?? "none";

        Telemetry.TagRequest(httpContext, "intake.outcome", failure.Outcome);
        Telemetry.TagRequest(httpContext, "intake.failure.cause", cause);
        Activity.Current?.RecordFailure(ex, failure.Outcome);
        logger.RequestFailed(failure.Level, failure.Logged, failure.Outcome, failure.Status, failure.Detail, cause, failure.Hint);

        if (failure.RetryAfterSeconds is { } seconds)
            httpContext.Response.Headers.RetryAfter = seconds.ToString();
        return Problem(failure.Status, failure.Detail);
    }

    public IResult ToResult(Exception ex, long bytes, DocumentClassification? classification, HttpContext httpContext)
    {
        var kind = classification?.Kind.ToString() ?? "unclassified";
        var failure = Describe(ex);
        var cause = ex.InnerException?.GetType().Name ?? "none";

        Telemetry.TagRequest(httpContext, "intake.outcome", failure.Outcome);
        Telemetry.TagRequest(httpContext, "intake.document.kind", kind);
        Telemetry.TagRequest(httpContext, "intake.failure.cause", cause);
        Activity.Current?.RecordFailure(ex, failure.Outcome);

        // Sizes, kinds and the reason only: never the document's text or the provider's error text.
        logger.DocumentFailed(failure.Level, failure.Logged, failure.Outcome, bytes, kind, failure.Status, failure.Detail, cause, failure.Hint);
        metrics.RecordDocument(kind, "none", failure.Status);

        if (failure.RetryAfterSeconds is { } seconds)
            httpContext.Response.Headers.RetryAfter = seconds.ToString();
        return Problem(failure.Status, failure.Detail);
    }
}

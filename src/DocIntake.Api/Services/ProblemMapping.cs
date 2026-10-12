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
public sealed class ProblemMapping(ILogger<ProblemMapping> logger, DocumentMetrics metrics)
{
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
        var cause = ex.InnerException?.GetType().Name ?? "none";

        Telemetry.TagRequest(httpContext, "intake.outcome", failure.Outcome);
        Telemetry.TagRequest(httpContext, "intake.failure.cause", cause);
        Activity.Current?.RecordFailure(ex, failure.Outcome);
        logger.Log(failure.Level, failure.Logged, "{Outcome}: request answered {Status}: {Reason} (cause {Cause}){Hint}",
            failure.Outcome, failure.Status, failure.Detail, cause, failure.Hint);

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
        logger.Log(failure.Level, failure.Logged, "{Outcome}: {Bytes}-byte {Kind} document answered {Status}: {Reason} (cause {Cause}){Hint}",
            failure.Outcome, bytes, kind, failure.Status, failure.Detail, cause, failure.Hint);
        metrics.RecordDocument(kind, "none", failure.Status);

        if (failure.RetryAfterSeconds is { } seconds)
            httpContext.Response.Headers.RetryAfter = seconds.ToString();
        return Problem(failure.Status, failure.Detail);
    }
}

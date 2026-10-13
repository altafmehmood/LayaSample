using DocIntake.Contracts;

namespace DocIntake.DocumentService;

/// <summary>Every log line of the document service. EventIds 2000 to 2999. Sizes, kinds and reasons only: never a file name or its text.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 2001, EventName = "RateLimited", Level = LogLevel.Warning, Message = "Document request rejected: every slot and queue place is taken")]
    public static partial void RateLimited(this ILogger logger);

    [LoggerMessage(EventId = 2010, EventName = "Classified", Level = LogLevel.Information,
        Message = "Classified a {Bytes}-byte upload as {Kind}: {Strategy}, {Pages} page(s)")]
    public static partial void Classified(this ILogger logger, long bytes, DocumentKind kind, PreparationStrategy strategy, int pages);

    [LoggerMessage(EventId = 2011, EventName = "Prepared", Level = LogLevel.Information, Message = "Prepared a {Bytes}-byte {Kind} document: {Strategy}, {Parts} part(s)")]
    public static partial void Prepared(this ILogger logger, long bytes, DocumentKind kind, PreparationStrategy strategy, int parts);

    [LoggerMessage(EventId = 2020, EventName = "UploadRefused", Level = LogLevel.Information, Message = "Refused a {Bytes}-byte upload ({Kind}): {Reason}")]
    public static partial void UploadRefused(this ILogger logger, Exception? exception, long bytes, FailureKind kind, string reason);

    [LoggerMessage(EventId = 2021, EventName = "UploadFailed", Level = LogLevel.Error, Message = "Could not handle a {Bytes}-byte upload")]
    public static partial void UploadFailed(this ILogger logger, Exception exception, long bytes);

    [LoggerMessage(EventId = 2030, EventName = "PageBudgetReached", Level = LogLevel.Warning,
        Message = "Page budget of {Max} renders reached: {Skipped} of {Pages} page(s) are not in the result")]
    public static partial void PageBudgetReached(this ILogger logger, int max, int skipped, int pages);

    [LoggerMessage(EventId = 2031, EventName = "PagesRendered", Level = LogLevel.Information,
        Message = "Rendered {Pages} of {Requested} requested page(s) of a {Bytes}-byte {MediaType} document ({Ocr} with OCR)")]
    public static partial void PagesRendered(this ILogger logger, int pages, int requested, long bytes, string mediaType, int ocr);

    [LoggerMessage(EventId = 2032, EventName = "AttachmentSkipped", Level = LogLevel.Warning,
        Message = "An embedded {Extension} file could not be prepared and was skipped")]
    public static partial void AttachmentSkipped(this ILogger logger, Exception exception, string extension);
}

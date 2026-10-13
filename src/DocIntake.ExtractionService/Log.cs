using DocIntake.Contracts;
using Microsoft.Extensions.Logging;
namespace DocIntake.ExtractionService;

/// <summary>Every log line of the extraction service. EventIds 3000 to 3999. Counts and kinds only: never names, values, prompts or answers.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 3001, EventName = "RateLimited", Level = LogLevel.Warning, Message = "Extraction request rejected: every slot and queue place is taken")]
    public static partial void RateLimited(this ILogger logger);

    [LoggerMessage(EventId = 3002, EventName = "ExtractionFailed", Level = LogLevel.Warning,
        Message = "Extraction failed with {Kind}: {Reason} (cause {Cause}, {Calls} model call(s))")]
    public static partial void ExtractionFailed(this ILogger logger, FailureKind kind, string reason, string cause, int calls);

    [LoggerMessage(EventId = 3010, EventName = "GenericRetry", Level = LogLevel.Information, Message = "{Agent} found almost nothing; retrying with the generic agent")]
    public static partial void GenericRetry(this ILogger logger, string agent);

    [LoggerMessage(EventId = 3011, EventName = "ExtractionWarnings", Level = LogLevel.Warning,
        Message = "Extraction of {Type} finished with {Count} warning(s): {Warnings}")]
    public static partial void ExtractionWarnings(this ILogger logger, LendingDocumentType type, int count, IReadOnlyList<string> warnings);

    [LoggerMessage(EventId = 3012, EventName = "Extracted", Level = LogLevel.Information,
        Message = "Extracted {Type} with {Agent} ({Model}): {Found}/{Total} fields, {In} in / {Out} out tokens")]
    public static partial void Extracted(this ILogger logger, LendingDocumentType type, string agent, string model, int found, int total, long @in, long @out);

    [LoggerMessage(EventId = 3020, EventName = "ModelCall", Level = LogLevel.Information,
        Message = "Model call {Outcome} on {Model} (attempt {Attempt}): {In} in ({Cached} cached) / {Out} out tokens, cost {Cost}")]
    public static partial void ModelCall(this ILogger logger, string outcome, string model, int attempt, long? @in, long? cached, long? @out, string cost);
}

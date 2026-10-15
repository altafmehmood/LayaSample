using DocIntake.Contracts;
using Microsoft.Extensions.Logging;

namespace DocIntake.Api;

/// <summary>
/// Every log line the gateway writes. EventIds are stable and grouped: 1000 gateway, 5000 security, 6000 usage. Parameters carry
/// sizes, kinds and reasons only, never a file name, document text, prompt or answer.
/// </summary>
internal static partial class Log
{
    // 1000: the gateway
    [LoggerMessage(EventId = 1001, EventName = "RateLimited", Level = LogLevel.Warning,
        Message = "Rejected {Path}: every {Policy} slot and queue place is taken (waited {WaitedMs:F0} ms)")]
    public static partial void RateLimited(this ILogger logger, string? path, string policy, double waitedMs);

    [LoggerMessage(EventId = 1002, EventName = "DocumentAnalysed", Level = LogLevel.Information,
        Message = "Analysed {Bytes}-byte {Kind} document: {Strategy}, {Pages} page(s), {Parts} part(s) in {ElapsedMs:F0} ms")]
    public static partial void DocumentAnalysed(this ILogger logger, long bytes, DocumentKind kind, PreparationStrategy strategy, int pages, int parts, double elapsedMs);

    [LoggerMessage(EventId = 1010, EventName = "RequestFailed",
        Message = "{Outcome}: request answered {Status}: {Reason} (cause {Cause}){Hint}")]
    public static partial void RequestFailed(this ILogger logger, LogLevel level, Exception? exception, string outcome, int status, string reason, string cause, string hint);

    [LoggerMessage(EventId = 1011, EventName = "DocumentFailed",
        Message = "{Outcome}: {Bytes}-byte {Kind} document answered {Status}: {Reason} (cause {Cause}){Hint}")]
    public static partial void DocumentFailed(this ILogger logger, LogLevel level, Exception? exception, string outcome, long bytes, string kind, int status, string reason, string cause, string hint);

    [LoggerMessage(EventId = 1020, EventName = "Started", Level = LogLevel.Information,
        Message = "DocIntake API {Version} started in {Environment}: document service {DocumentService}, extraction service {Extraction}, "
                  + "{Analyses} analyses and {Extractions} extractions at once, telemetry export {Telemetry}")]
    public static partial void Started(this ILogger logger, string version, string environment, string? documentService, string extraction,
        int analyses, int extractions, string telemetry);

    [LoggerMessage(EventId = 1030, EventName = "ReadinessChanged", Message = "{Component} readiness changed from {Before} to {Now}")]
    public static partial void ReadinessChanged(this ILogger logger, LogLevel level, string component, string before, string now);

    // 5000: security
    [LoggerMessage(EventId = 5001, EventName = "ApiKeyRejected", Level = LogLevel.Warning, Message = "Rejected a request to {Path}: API key {Reason}")]
    public static partial void ApiKeyRejected(this ILogger logger, string? path, string reason);

    [LoggerMessage(EventId = 5002, EventName = "MalwareFound", Level = LogLevel.Warning, Message = "Malware found in a {Bytes}-byte upload: {Signature}")]
    public static partial void MalwareFound(this ILogger logger, long bytes, string? signature);

    [LoggerMessage(EventId = 5003, EventName = "AcceptedWithoutVerdict", Level = LogLevel.Warning,
        Message = "Accepting a {Bytes}-byte upload without a malware verdict (FailMode is open): {Reason}")]
    public static partial void AcceptedWithoutVerdict(this ILogger logger, long bytes, string? reason);

    [LoggerMessage(EventId = 5004, EventName = "ScannerNoVerdict", Level = LogLevel.Warning, Message = "Malware scan service at {Url} gave no verdict: {Reason}")]
    public static partial void ScannerNoVerdict(this ILogger logger, string url, string reason);

    [LoggerMessage(EventId = 5010, EventName = "AnswerRejected", Level = LogLevel.Warning,
        Message = "Rejected an extraction whose answer repeated instruction-like text ({Count} finding(s))")]
    public static partial void AnswerRejected(this ILogger logger, int count);

    [LoggerMessage(EventId = 5011, EventName = "BlockingFindings", Level = LogLevel.Warning, Message = "Upload has {Count} blocking finding(s): {Kinds} ({Mode})")]
    public static partial void BlockingFindings(this ILogger logger, int count, string kinds, string mode);

    // 6000: usage
    [LoggerMessage(EventId = 6001, EventName = "UsageDropped", Level = LogLevel.Error, Message = "Usage queue is full or closed; dropped a record for {Model}")]
    public static partial void UsageDropped(this ILogger logger, string model);

    [LoggerMessage(EventId = 6002, EventName = "UsageNotStored", Level = LogLevel.Error, Message = "Could not store {Count} usage record(s)")]
    public static partial void UsageNotStored(this ILogger logger, Exception exception, int count);

    [LoggerMessage(EventId = 6003, EventName = "UsageHistoryLocation", Level = LogLevel.Information, Message = "Usage history is kept in {Path}")]
    public static partial void UsageHistoryLocation(this ILogger logger, string path);
}

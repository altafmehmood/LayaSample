namespace DocIntake.ScanService;

/// <summary>Every log line of the scan service. EventIds 7300 to 7399. Sizes and reasons only: never a file name or its content.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 7301, EventName = "ScannerNoVerdict", Level = LogLevel.Warning, Message = "clamd at {Host}:{Port} gave no verdict: {Reason}")]
    public static partial void ScannerNoVerdict(this ILogger logger, string host, int port, string reason);

    [LoggerMessage(EventId = 7302, EventName = "Scanned", Level = LogLevel.Information, Message = "Scanned a {Bytes}-byte document: {Outcome}")]
    public static partial void Scanned(this ILogger logger, long? bytes, string outcome);

    [LoggerMessage(EventId = 7303, EventName = "ReadinessChanged", Message = "{Component} readiness changed from {Before} to {Now}")]
    public static partial void ReadinessChanged(this ILogger logger, LogLevel level, string component, string before, string now);
}

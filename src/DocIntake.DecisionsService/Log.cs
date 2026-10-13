using Microsoft.Extensions.Logging;
namespace DocIntake.DecisionsService;

/// <summary>Every log line of the decisions service. EventIds 4000 to 4999 (7000 for hosting and warm-up).</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 7101, EventName = "ModelLoading", Level = LogLevel.Information,
        Message = "Loading Laya model (downloaded on first run unless Laya:ModelPath points at a local copy)...")]
    public static partial void ModelLoading(this ILogger logger);

    [LoggerMessage(EventId = 7102, EventName = "ModelReady", Level = LogLevel.Information, Message = "Laya model ready after {ElapsedMs:F0} ms and {Attempts} attempt(s).")]
    public static partial void ModelReady(this ILogger logger, double elapsedMs, int attempts);

    [LoggerMessage(EventId = 7103, EventName = "ModelFailed", Level = LogLevel.Error,
        Message = "Laya model failed to load; feedback analysis is unavailable until restart.")]
    public static partial void ModelFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7104, EventName = "ModelRetrying", Level = LogLevel.Warning, Message = "Laya model failed to load; retrying in {Delay}.")]
    public static partial void ModelRetrying(this ILogger logger, Exception exception, TimeSpan delay);
}

using Microsoft.Extensions.Logging;
namespace DocIntake.Rendering;

/// <summary>Log lines of the rendering library (document service). EventIds 7000s: hosting and warm-up.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 7201, EventName = "OcrReady", Level = LogLevel.Information, Message = "OCR models ready ({Model}) in {ElapsedMs:F0} ms.")]
    public static partial void OcrReady(this ILogger logger, OcrModel model, double elapsedMs);

    [LoggerMessage(EventId = 7202, EventName = "OcrFailed", Level = LogLevel.Error, Message = "OCR models failed to load; documents that need OCR get 503.")]
    public static partial void OcrFailed(this ILogger logger, Exception exception);
}

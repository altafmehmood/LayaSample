using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocIntake.Rendering;

/// <summary>
/// Loads the OCR models at startup, so a missing or broken model shows up in readiness and the logs instead of on
/// the first scanned document. A failure is not final: <see cref="RapidOcrEngine"/> retries on the next call.
/// </summary>
public sealed class OcrWarmup(IOcrEngine ocr, IOptions<RenderingOptions> options, ILogger<OcrWarmup> logger) : BackgroundService
{
    private volatile WarmupState _state;

    public bool Enabled { get; } = options.Value.EnableOcr;
    public WarmupState State => _state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        using var span = IntakeTelemetry.Source.StartActivity("ocr.model.load");
        span?.SetTag("intake.ocr.model", options.Value.OcrModel.ToString());
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var outcome = "ready";
        try
        {
            // Model loading is synchronous; keep it off the startup path.
            await Task.Run(ocr.EnsureLoaded, stoppingToken);
            _state = WarmupState.Ready;
            logger.LogInformation("OCR models ready ({Model}) in {ElapsedMs:F0} ms.", options.Value.OcrModel,
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OcrUnavailableException ex)
        {
            _state = WarmupState.Failed;
            outcome = "failed";
            span.RecordFailure(ex);
            logger.LogError(ex, "OCR models failed to load; documents that need OCR get 503.");
        }
        finally
        {
            RenderingMetrics.ModelLoadDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }
}

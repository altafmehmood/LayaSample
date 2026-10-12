using DocIntake.ServiceDefaults;
using ElBruno.LocalLLMs.Decisions;
using DocIntake.Api.Services.Documents;

namespace DocIntake.Api.Services;

/// <summary>
/// Loads the Laya model at startup so the first request doesn't pay the download/load cost. A failed load (usually a
/// dropped download) is retried with backoff before the model is reported as failed.
/// </summary>
public sealed class ModelWarmup(IDecisionClient client, DocumentMetrics metrics, ILogger<ModelWarmup> logger) : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    private volatile WarmupState _state;

    public WarmupState State => _state;
    public bool Ready => _state == WarmupState.Ready;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var span = Telemetry.Source.StartActivity("intake.model.load");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var attempt = 0; ; attempt++)
        {
            span?.SetTag("intake.model.attempts", attempt + 1);
            logger.LogInformation("Loading Laya model (downloaded on first run unless Laya:ModelPath points at a local copy)...");
            try
            {
                await client.IsTrueAsync("warm-up", "This is a warm-up.", stoppingToken);
                _state = WarmupState.Ready;
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                metrics.RecordModelLoad(elapsed, "ready");
                logger.LogInformation("Laya model ready after {ElapsedMs:F0} ms and {Attempts} attempt(s).", elapsed.TotalMilliseconds, attempt + 1);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == RetryDelays.Length)
                {
                    _state = WarmupState.Failed;
                    metrics.RecordModelLoad(System.Diagnostics.Stopwatch.GetElapsedTime(started), "failed");
                    span.RecordFailure(ex);
                    logger.LogError(ex, "Laya model failed to load; feedback analysis is unavailable until restart.");
                    return;
                }
                logger.LogWarning(ex, "Laya model failed to load; retrying in {Delay}.", RetryDelays[attempt]);
                await Task.Delay(RetryDelays[attempt], stoppingToken);
            }
        }
    }
}

using System.Diagnostics;
using DocIntake.ServiceDefaults;
using ElBruno.LocalLLMs.Decisions;

namespace DocIntake.DecisionsService;

/// <summary>Whether the model can answer. A seam, so tests can say "ready" without loading an 850 MB model.</summary>
public interface IModelStatus
{
    WarmupState State { get; }
}

/// <summary>
/// Loads the Laya model at startup so the first request doesn't pay the download/load cost. A failed load (usually a
/// dropped download) is retried with backoff before the model is reported as failed.
/// </summary>
public sealed class ModelWarmup(IDecisionClient client, DecisionsMetrics metrics, ILogger<ModelWarmup> logger) : BackgroundService, IModelStatus
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    private volatile WarmupState _state;

    public WarmupState State => _state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var span = Telemetry.Source.StartActivity("intake.model.load");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var attempt = 0; ; attempt++)
        {
            span?.SetTag("intake.model.attempts", attempt + 1);
            span?.AddEvent(new ActivityEvent("intake.model.load.attempt", tags: new ActivityTagsCollection { ["intake.model.attempt"] = attempt + 1 }));
            logger.ModelLoading();
            try
            {
                await client.IsTrueAsync("warm-up", "This is a warm-up.", stoppingToken);
                _state = WarmupState.Ready;
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                metrics.RecordModelLoad(elapsed, "ready");
                logger.ModelReady(elapsed.TotalMilliseconds, attempt + 1);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == RetryDelays.Length)
                {
                    _state = WarmupState.Failed;
                    metrics.RecordModelLoad(System.Diagnostics.Stopwatch.GetElapsedTime(started), "failed");
                    span.RecordFailure(ex);
                    logger.ModelFailed(ex);
                    return;
                }
                logger.ModelRetrying(ex, RetryDelays[attempt]);
                await Task.Delay(RetryDelays[attempt], stoppingToken);
            }
        }
    }
}

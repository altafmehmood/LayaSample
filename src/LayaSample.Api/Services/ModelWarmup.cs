using ElBruno.LocalLLMs.Decisions;

namespace LayaSample.Api.Services;

/// <summary>Loads the model at startup so the first request doesn't pay the download/load cost.</summary>
public sealed class ModelWarmup(IDecisionClient client, ILogger<ModelWarmup> logger) : BackgroundService
{
    public bool Ready { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Loading Laya model (downloaded on first run unless Laya:ModelPath points at a local copy)...");
        try
        {
            await client.IsTrueAsync("warm-up", "This is a warm-up.", stoppingToken);
            Ready = true;
            logger.LogInformation("Laya model ready.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Laya model failed to load.");
        }
    }
}

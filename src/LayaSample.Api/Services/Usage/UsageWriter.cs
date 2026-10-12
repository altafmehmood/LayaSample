using System.Threading.Channels;
using LayaSample.Api.Services.Documents;

namespace LayaSample.Api.Services.Usage;

/// <summary>
/// Takes usage records from requests and stores them in batches on a background thread, so recording costs a request
/// nothing and a store failure never fails an extraction (it is logged and counted). Records still queued are written on
/// shutdown.
/// </summary>
public sealed class UsageWriter : BackgroundService, IUsageRecorder
{
    private const int QueueCapacity = 10_000;
    private const int BatchSize = 100;

    private readonly Channel<UsageRecord> _queue = Channel.CreateBounded<UsageRecord>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly UsageStore _store;
    private readonly DocumentMetrics _metrics;
    private readonly ILogger<UsageWriter> _logger;

    public UsageWriter(UsageStore store, DocumentMetrics metrics, ILogger<UsageWriter> logger)
    {
        _store = store;
        _metrics = metrics;
        _logger = logger;
    }

    public void Record(UsageRecord record)
    {
        if (!_store.Enabled)
            return;
        if (!_queue.Writer.TryWrite(record))
        {
            _metrics.RecordUsageWriteFailure(1);
            _logger.LogError("Usage queue is full or closed; dropped a record for {Model}", record.Model);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not the stopping token: shutdown completes the queue instead, and the loop ends once it is empty.
        var batch = new List<UsageRecord>(BatchSize);
        while (await _queue.Reader.WaitToReadAsync(CancellationToken.None))
        {
            batch.Clear();
            while (batch.Count < BatchSize && _queue.Reader.TryRead(out var record))
                batch.Add(record);
            await WriteAsync(batch);
        }
    }

    private async Task WriteAsync(List<UsageRecord> batch)
    {
        try
        {
            await _store.InsertAsync(batch, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _metrics.RecordUsageWriteFailure(batch.Count);
            _logger.LogError(ex, "Could not store {Count} usage record(s)", batch.Count);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }
}

public static class UsageServiceExtensions
{
    public static IServiceCollection AddUsageTracking(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<UsageOptions>().Bind(configuration.GetSection(UsageOptions.Section));
        services.AddOptions<PricingOptions>().Bind(configuration.GetSection(PricingOptions.Section));
        services.AddSingleton<ModelPricing>();
        services.AddSingleton<UsageStore>();
        services.AddSingleton<UsageWriter>();
        services.AddSingleton<IUsageRecorder>(sp => sp.GetRequiredService<UsageWriter>());
        services.AddHostedService(sp => sp.GetRequiredService<UsageWriter>());
        return services;
    }
}

using System.Diagnostics.Metrics;

namespace DocIntake.DecisionsService;

/// <summary>Model metrics on the <c>DocIntake.Decisions</c> meter. Exported over OTLP when an endpoint is configured.</summary>
public sealed class DecisionsMetrics
{
    public const string MeterName = "DocIntake.Decisions";

    private readonly Histogram<double> _modelLoad;

    public DecisionsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _modelLoad = meter.CreateHistogram<double>("intake.model.load.duration", "s", "Time to load the Laya model at startup, by outcome.");
    }

    public void RecordModelLoad(TimeSpan elapsed, string outcome) =>
        _modelLoad.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));
}

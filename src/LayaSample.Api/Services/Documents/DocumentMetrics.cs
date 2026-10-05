using System.Diagnostics.Metrics;

namespace LayaSample.Api.Services.Documents;

/// <summary>
/// Document analysis metrics on the <c>LayaSample.Documents</c> meter. Readable with <c>dotnet-counters</c> or any
/// OpenTelemetry exporter subscribed to the meter; no exporter is configured here.
/// </summary>
public sealed class DocumentMetrics
{
    public const string MeterName = "LayaSample.Documents";

    private readonly Histogram<double> _stageDuration;
    private readonly Counter<long> _documents;

    public DocumentMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _stageDuration = meter.CreateHistogram<double>("laya.document.stage.duration", "s",
            "Time spent in one analysis stage (classify, prepare, route).");
        _documents = meter.CreateCounter<long>("laya.document.analyzed", "{document}",
            "Documents analysed, by kind, applied strategy and outcome (HTTP status).");
    }

    public void RecordStage(string stage, TimeSpan elapsed) =>
        _stageDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("stage", stage));

    public void RecordDocument(string kind, string strategy, int status) =>
        _documents.Add(1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("strategy", strategy),
            new KeyValuePair<string, object?>("status", status));
}

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
    private readonly Counter<long> _extractions;
    private readonly Counter<long> _tokens;
    private readonly Counter<long> _authRejections;

    public DocumentMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _stageDuration = meter.CreateHistogram<double>("laya.document.stage.duration", "s",
            "Time spent in one analysis stage (classify, prepare, route).");
        _documents = meter.CreateCounter<long>("laya.document.analyzed", "{document}",
            "Documents analysed, by kind, applied strategy and outcome (HTTP status).");
        _extractions = meter.CreateCounter<long>("laya.extraction.documents", "{document}",
            "Documents extracted, by agent, document type and model.");
        _tokens = meter.CreateCounter<long>("laya.extraction.tokens", "{token}",
            "Model tokens used by extraction, by model and direction.");
        _authRejections = meter.CreateCounter<long>("laya.auth.rejections", "{request}",
            "Requests rejected for a missing, invalid or unconfigured API key.");
    }

    public void RecordStage(string stage, TimeSpan elapsed) =>
        _stageDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("stage", stage));

    public void RecordDocument(string kind, string strategy, int status) =>
        _documents.Add(1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("strategy", strategy),
            new KeyValuePair<string, object?>("status", status));

    public void RecordExtraction(string agent, string documentType, string model, long inputTokens, long outputTokens)
    {
        _extractions.Add(1,
            new KeyValuePair<string, object?>("agent", agent),
            new KeyValuePair<string, object?>("document_type", documentType),
            new KeyValuePair<string, object?>("model", model));
        _tokens.Add(inputTokens, new KeyValuePair<string, object?>("model", model), new KeyValuePair<string, object?>("direction", "input"));
        _tokens.Add(outputTokens, new KeyValuePair<string, object?>("model", model), new KeyValuePair<string, object?>("direction", "output"));
    }

    public void RecordAuthRejection(string reason) =>
        _authRejections.Add(1, new KeyValuePair<string, object?>("reason", reason));
}

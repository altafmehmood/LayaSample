using System.Diagnostics.Metrics;

namespace LayaSample.Api.Services.Documents;

/// <summary>
/// Document analysis metrics on the <c>LayaSample.Documents</c> meter. Exported over OTLP when an endpoint is configured
/// (see <c>LayaTelemetry</c>); also readable with <c>dotnet-counters</c>.
/// </summary>
public sealed class DocumentMetrics
{
    public const string MeterName = "LayaSample.Documents";

    private readonly Histogram<double> _stageDuration;
    private readonly Counter<long> _documents;
    private readonly Counter<long> _extractions;
    private readonly Counter<long> _tokens;
    private readonly Counter<long> _authRejections;
    private readonly Histogram<double> _extractionDuration;
    private readonly UpDownCounter<long> _extractionsInFlight;
    private readonly Histogram<long> _documentBytes;
    private readonly Histogram<int> _documentPages;
    private readonly Histogram<double> _queueWait;
    private readonly Counter<long> _rateLimited;
    private readonly Counter<long> _llmRetries;
    private readonly Counter<long> _extractionFields;
    private readonly Counter<long> _extractionWarnings;
    private readonly Histogram<double> _modelLoad;

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
        _extractionDuration = meter.CreateHistogram<double>("laya.extraction.duration", "s",
            "One extraction (all model calls for a document), by agent, model and outcome.");
        _extractionsInFlight = meter.CreateUpDownCounter<long>("laya.extraction.inflight", "{extraction}",
            "Extractions running now.");
        _documentBytes = meter.CreateHistogram<long>("laya.document.size", "By", "Size of uploaded documents, by kind.");
        _documentPages = meter.CreateHistogram<int>("laya.document.pages", "{page}", "Pages in uploaded documents, by kind.");
        _queueWait = meter.CreateHistogram<double>("laya.request.queue.wait", "s",
            "Time from the request starting to its handler running: upload plus waiting for a rate-limit slot.");
        _rateLimited = meter.CreateCounter<long>("laya.ratelimit.rejections", "{request}",
            "Requests turned away with 503 because every slot and queue place was taken, by policy.");
        _llmRetries = meter.CreateCounter<long>("laya.llm.retries", "{retry}",
            "Model-provider calls repeated by the SDK, by reason.");
        _extractionFields = meter.CreateCounter<long>("laya.extraction.fields", "{field}",
            "Fields in extraction results, by agent and whether a value was found.");
        _extractionWarnings = meter.CreateCounter<long>("laya.extraction.warnings", "{warning}",
            "Warnings returned with extraction results (validation, cut text, skipped images).");
        _modelLoad = meter.CreateHistogram<double>("laya.model.load.duration", "s", "Time to load the Laya model at startup, by outcome.");
    }

    public void RecordExtractionRun(string agent, string model, TimeSpan elapsed, string outcome) =>
        _extractionDuration.Record(elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("agent", agent),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("outcome", outcome));

    public void ExtractionStarted() => _extractionsInFlight.Add(1);
    public void ExtractionFinished() => _extractionsInFlight.Add(-1);

    public void RecordUpload(string kind, long bytes, int pages)
    {
        _documentBytes.Record(bytes, new KeyValuePair<string, object?>("kind", kind));
        _documentPages.Record(pages, new KeyValuePair<string, object?>("kind", kind));
    }

    public void RecordQueueWait(string endpoint, TimeSpan wait) =>
        _queueWait.Record(wait.TotalSeconds, new KeyValuePair<string, object?>("endpoint", endpoint));

    public void RecordRateLimited(string policy) =>
        _rateLimited.Add(1, new KeyValuePair<string, object?>("policy", policy));

    public void RecordLlmRetry(string reason) =>
        _llmRetries.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void RecordFields(string agent, int found, int total)
    {
        _extractionFields.Add(found, new KeyValuePair<string, object?>("agent", agent), new KeyValuePair<string, object?>("found", true));
        _extractionFields.Add(total - found, new KeyValuePair<string, object?>("agent", agent), new KeyValuePair<string, object?>("found", false));
    }

    public void RecordWarnings(string agent, int count) =>
        _extractionWarnings.Add(count, new KeyValuePair<string, object?>("agent", agent));

    public void RecordModelLoad(TimeSpan elapsed, string outcome) =>
        _modelLoad.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));

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

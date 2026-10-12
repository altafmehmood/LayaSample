using DocIntake.ServiceDefaults;
using System.Diagnostics.Metrics;
using DocIntake.Contracts;

namespace DocIntake.Api.Services.Documents;

/// <summary>
/// Document analysis metrics on the <c>DocIntake.Documents</c> meter. Exported over OTLP when an endpoint is configured
/// (see <c>IntakeTelemetry</c>); also readable with <c>dotnet-counters</c>.
/// </summary>
public sealed class DocumentMetrics
{
    public const string MeterName = "DocIntake.Documents";

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
    private readonly Counter<double> _llmCost;
    private readonly Counter<long> _scans;
    private readonly Counter<long> _risks;
    private readonly Histogram<double> _scanDuration;
    private readonly Counter<long> _usageWriteFailures;
    private readonly Counter<long> _usageUnpriced;

    public DocumentMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _stageDuration = meter.CreateHistogram<double>("intake.document.stage.duration", "s",
            "Time spent in one analysis stage (classify, prepare, route).");
        _documents = meter.CreateCounter<long>("intake.document.analyzed", "{document}",
            "Documents analysed, by kind, applied strategy and outcome (HTTP status).");
        _extractions = meter.CreateCounter<long>("intake.extraction.documents", "{document}",
            "Documents extracted, by agent, document type and model.");
        _tokens = meter.CreateCounter<long>("intake.extraction.tokens", "{token}",
            "Model tokens used by extraction calls, by model and direction (input, cached, reasoning, output). Cached tokens are part of input, reasoning ones part of output.");
        _authRejections = meter.CreateCounter<long>("intake.auth.rejections", "{request}",
            "Requests rejected for a missing, invalid or unconfigured API key.");
        _extractionDuration = meter.CreateHistogram<double>("intake.extraction.duration", "s",
            "One extraction (all model calls for a document), by agent, model and outcome.");
        _extractionsInFlight = meter.CreateUpDownCounter<long>("intake.extraction.inflight", "{extraction}",
            "Extractions running now.");
        _documentBytes = meter.CreateHistogram<long>("intake.document.size", "By", "Size of uploaded documents, by kind.");
        _documentPages = meter.CreateHistogram<int>("intake.document.pages", "{page}", "Pages in uploaded documents, by kind.");
        _queueWait = meter.CreateHistogram<double>("intake.request.queue.wait", "s",
            "Time from the request starting to its handler running: upload plus waiting for a rate-limit slot.");
        _rateLimited = meter.CreateCounter<long>("intake.ratelimit.rejections", "{request}",
            "Requests turned away with 503 because every slot and queue place was taken, by policy.");
        _llmRetries = meter.CreateCounter<long>("intake.llm.retries", "{retry}",
            "Model-provider calls repeated by the SDK, by reason.");
        _extractionFields = meter.CreateCounter<long>("intake.extraction.fields", "{field}",
            "Fields in extraction results, by agent and whether a value was found.");
        _extractionWarnings = meter.CreateCounter<long>("intake.extraction.warnings", "{warning}",
            "Warnings returned with extraction results (validation, cut text, skipped images).");
        _modelLoad = meter.CreateHistogram<double>("intake.model.load.duration", "s", "Time to load the Laya model at startup, by outcome.");
        _llmCost = meter.CreateCounter<double>("intake.llm.cost", "USD", "List-price cost of model calls, by model.");
        _risks = meter.CreateCounter<long>("intake.risk.findings", "{finding}", "Findings about uploads and answers (active content, macros, hidden text, prompt injection), by kind and severity.");
        _scans = meter.CreateCounter<long>("intake.scan.results", "{upload}", "Uploads scanned for malware, by result (clean, infected, unavailable).");
        _scanDuration = meter.CreateHistogram<double>("intake.scan.duration", "s", "Time the malware scanner took per upload, by result.");
        _usageWriteFailures = meter.CreateCounter<long>("intake.usage.write_failures", "{record}",
            "Usage records that could not be stored (store failure or full queue).");
        _usageUnpriced = meter.CreateCounter<long>("intake.usage.unpriced", "{call}",
            "Model calls whose model has no configured price, so their cost is unknown.");
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

    public void RecordExtraction(string agent, string documentType, string model) =>
        _extractions.Add(1,
            new KeyValuePair<string, object?>("agent", agent),
            new KeyValuePair<string, object?>("document_type", documentType),
            new KeyValuePair<string, object?>("model", model));

    /// <summary>One model call, so a generic-agent retry on another model is attributed to the right model and price.</summary>
    public void RecordLlmCall(string model, ExtractionUsage usage, decimal? costUsd)
    {
        void Add(string direction, long tokens)
        {
            if (tokens > 0)
                _tokens.Add(tokens, new KeyValuePair<string, object?>("model", model), new KeyValuePair<string, object?>("direction", direction));
        }
        Add("input", usage.InputTokens);
        Add("cached", usage.CachedInputTokens);
        Add("reasoning", usage.ReasoningTokens ?? 0);
        Add("output", usage.OutputTokens);
        if (costUsd is { } cost)
            _llmCost.Add((double)cost, new KeyValuePair<string, object?>("model", model));
        else
            _usageUnpriced.Add(1, new KeyValuePair<string, object?>("model", model));
    }

    public void RecordRisk(string kind, string severity) =>
        _risks.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("severity", severity));

    public void RecordScan(string result, TimeSpan elapsed)
    {
        _scans.Add(1, new KeyValuePair<string, object?>("result", result));
        _scanDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("result", result));
    }

    public void RecordUsageWriteFailure(int records) => _usageWriteFailures.Add(records);

    public void RecordAuthRejection(string reason) =>
        _authRejections.Add(1, new KeyValuePair<string, object?>("reason", reason));
}

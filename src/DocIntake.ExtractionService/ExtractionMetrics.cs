using System.Diagnostics.Metrics;
using DocIntake.Contracts;

namespace DocIntake.ExtractionService;

/// <summary>
/// Extraction and model-call metrics on the <c>DocIntake.Extraction</c> meter. Exported over OTLP when an endpoint is
/// configured (see <c>IntakeTelemetry</c>); also readable with <c>dotnet-counters</c>.
/// </summary>
public sealed class ExtractionMetrics
{
    public const string MeterName = "DocIntake.Extraction";

    private readonly Counter<long> _extractions;
    private readonly Counter<long> _tokens;
    private readonly Histogram<double> _extractionDuration;
    private readonly UpDownCounter<long> _extractionsInFlight;
    private readonly Counter<long> _llmRetries;
    private readonly Counter<long> _extractionFields;
    private readonly Counter<long> _extractionWarnings;
    private readonly Counter<double> _llmCost;
    private readonly Counter<long> _usageUnpriced;

    public ExtractionMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _extractions = meter.CreateCounter<long>("intake.extraction.documents", "{document}",
            "Documents extracted, by agent, document type and model.");
        _tokens = meter.CreateCounter<long>("intake.extraction.tokens", "{token}",
            "Model tokens used by extraction calls, by model and direction (input, cached, reasoning, output). Cached tokens are part of input, reasoning ones part of output.");
        _extractionDuration = meter.CreateHistogram<double>("intake.extraction.duration", "s",
            "One extraction (all model calls for a document), by agent, model and outcome.");
        _extractionsInFlight = meter.CreateUpDownCounter<long>("intake.extraction.inflight", "{extraction}",
            "Extractions running now.");
        _llmRetries = meter.CreateCounter<long>("intake.llm.retries", "{retry}",
            "Model-provider calls repeated by the SDK, by reason.");
        _extractionFields = meter.CreateCounter<long>("intake.extraction.fields", "{field}",
            "Fields in extraction results, by agent and whether a value was found.");
        _extractionWarnings = meter.CreateCounter<long>("intake.extraction.warnings", "{warning}",
            "Warnings returned with extraction results (validation, cut text, skipped images).");
        _llmCost = meter.CreateCounter<double>("intake.llm.cost", "USD", "List-price cost of model calls, by model.");
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

    public void RecordLlmRetry(string reason) =>
        _llmRetries.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void RecordFields(string agent, int found, int total)
    {
        _extractionFields.Add(found, new KeyValuePair<string, object?>("agent", agent), new KeyValuePair<string, object?>("found", true));
        _extractionFields.Add(total - found, new KeyValuePair<string, object?>("agent", agent), new KeyValuePair<string, object?>("found", false));
    }

    public void RecordWarnings(string agent, int count) =>
        _extractionWarnings.Add(count, new KeyValuePair<string, object?>("agent", agent));

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
}

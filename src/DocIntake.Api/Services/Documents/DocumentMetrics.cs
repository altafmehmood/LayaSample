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
    private readonly Counter<long> _authRejections;
    private readonly Histogram<long> _documentBytes;
    private readonly Histogram<int> _documentPages;
    private readonly Histogram<double> _queueWait;
    private readonly Counter<long> _rateLimited;
    private readonly Counter<long> _scans;
    private readonly Counter<long> _risks;
    private readonly Histogram<double> _scanDuration;
    private readonly Counter<long> _usageWriteFailures;

    public DocumentMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _stageDuration = meter.CreateHistogram<double>("intake.document.stage.duration", "s",
            "Time spent in one analysis stage (classify, prepare, route).");
        _documents = meter.CreateCounter<long>("intake.document.analyzed", "{document}",
            "Documents analysed, by kind, applied strategy and outcome (HTTP status).");
        _authRejections = meter.CreateCounter<long>("intake.auth.rejections", "{request}",
            "Requests rejected for a missing, invalid or unconfigured API key.");
        _documentBytes = meter.CreateHistogram<long>("intake.document.size", "By", "Size of uploaded documents, by kind.");
        _documentPages = meter.CreateHistogram<int>("intake.document.pages", "{page}", "Pages in uploaded documents, by kind.");
        _queueWait = meter.CreateHistogram<double>("intake.request.queue.wait", "s",
            "Time from the request starting to its handler running: upload plus waiting for a rate-limit slot.");
        _rateLimited = meter.CreateCounter<long>("intake.ratelimit.rejections", "{request}",
            "Requests turned away with 503 because every slot and queue place was taken, by policy.");
        _risks = meter.CreateCounter<long>("intake.risk.findings", "{finding}", "Findings about uploads and answers (active content, macros, hidden text, prompt injection), by kind and severity.");
        _scans = meter.CreateCounter<long>("intake.scan.results", "{upload}", "Uploads scanned for malware, by result (clean, infected, unavailable).");
        _scanDuration = meter.CreateHistogram<double>("intake.scan.duration", "s", "Time the malware scanner took per upload, by result.");
        _usageWriteFailures = meter.CreateCounter<long>("intake.usage.write_failures", "{record}",
            "Usage records that could not be stored (store failure or full queue).");
    }

    public void RecordUpload(string kind, long bytes, int pages)
    {
        _documentBytes.Record(bytes, new KeyValuePair<string, object?>("kind", kind));
        _documentPages.Record(pages, new KeyValuePair<string, object?>("kind", kind));
    }

    public void RecordQueueWait(string endpoint, TimeSpan wait) =>
        _queueWait.Record(wait.TotalSeconds, new KeyValuePair<string, object?>("endpoint", endpoint));

    public void RecordRateLimited(string policy) =>
        _rateLimited.Add(1, new KeyValuePair<string, object?>("policy", policy));

    public void RecordStage(string stage, TimeSpan elapsed) =>
        _stageDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("stage", stage));

    public void RecordDocument(string kind, string strategy, int status) =>
        _documents.Add(1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("strategy", strategy),
            new KeyValuePair<string, object?>("status", status));

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

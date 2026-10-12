using DocIntake.ServiceDefaults;
using System.Diagnostics;
using DocIntake.Contracts;
using DocIntake.Api.Services.Security;
using Microsoft.Extensions.Options;

namespace DocIntake.Api.Services.Documents;

/// <summary>One upload's trip through the pipeline: stage timings plus what each stage produced so far.</summary>
public sealed class DocumentRun(DocumentMetrics metrics)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _stage = Stopwatch.GetTimestamp();

    public DocumentClassification? Classification { get; internal set; }
    public PreparedDocument? Prepared { get; internal set; }

    /// <summary>Everything found about the upload so far (structure, then text), reported in the response.</summary>
    public List<RiskFinding> Risks { get; } = [];

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    public void Lap(string name)
    {
        metrics.RecordStage(name, Stopwatch.GetElapsedTime(_stage));
        _stage = Stopwatch.GetTimestamp();
    }
}

/// <summary>Classify an upload and prepare it. Shared by every endpoint that works on an uploaded document.</summary>
public sealed class DocumentPipeline(IDocumentClassifier classifier, IDocumentPreparer preparer, IMalwareScanner scanner,
    RiskScanner riskScanner, IOptions<SecurityOptions> security, DocumentMetrics metrics, ILogger<DocumentPipeline> logger)
{
    public DocumentRun NewRun() => new(metrics);

    public async Task PrepareAsync(IFormFile file, DocumentRun run, CancellationToken ct)
    {
        // The document is one seekable stream, read in place by classify, prepare and the renderer hand-off; it is never
        // copied into memory. ASP.NET has already spooled the multipart file to a temporary file above 64 KB, and
        // PDF, ZIP and TIFF need the random access that stream gives.
        await using var upload = file.OpenReadStream();
        // Unreachable with ASP.NET's own form files; a custom IFormFile with a forward-only stream is spooled to memory.
        await using var buffered = upload.CanSeek ? null : new MemoryStream((int)file.Length);
        if (buffered is not null)
            await upload.CopyToAsync(buffered, ct);
        var stream = buffered ?? upload;
        Activity.Current?.SetTag("intake.file.bytes", file.Length);
        Activity.Current?.SetTag("intake.file.content_type", file.ContentType);

        // Before any parser sees the file: an infected upload is turned away without being read by PdfPig, the Office
        // libraries or the renderer.
        if (scanner.Enabled)
        {
            await ScanAsync(stream, file.Length, ct);
            run.Lap("scan");
        }

        using (var span = Telemetry.Source.StartActivity("intake.classify"))
        {
            run.Classification = await classifier.ClassifyAsync(stream, ct);
            span?.SetTag("intake.document.kind", run.Classification.Kind.ToString());
            span?.SetTag("intake.document.pages", run.Classification.PageCount);
            span?.SetTag("intake.document.strategy", run.Classification.Strategy.ToString());
            span?.SetTag("intake.document.route", run.Classification.Route);
            span?.SetTag("intake.document.is_xfa", run.Classification.IsXfa);
            span?.SetTag("intake.document.page_contents", string.Join(",", run.Classification.Pages.GroupBy(p => p.Content).Select(g => $"{g.Key}={g.Count()}")));
            metrics.RecordUpload(run.Classification.Kind.ToString(), file.Length, run.Classification.PageCount);
            // Before anything is rendered or read: a file with active content or hidden text is turned away here (with
            // OnFinding=reject) without going to the renderer.
            run.Risks.AddRange(riskScanner.CheckStructure(run.Classification));
        }
        run.Lap("classify");
        using (var span = Telemetry.Source.StartActivity("intake.prepare"))
        {
            try
            {
                run.Prepared = await preparer.PrepareAsync(stream, run.Classification, ct);
            }
            catch (Exception ex)
            {
                span.RecordFailure(ex);
                throw;
            }
            // The text the model will read, OCR included, checked for instructions addressed to it.
            run.Risks.AddRange(riskScanner.CheckText(run.Prepared));
            span?.SetTag("intake.prepare.parts", run.Prepared.Parts.Count);
            span?.SetTag("intake.prepare.part_roles", string.Join(",", run.Prepared.Parts.GroupBy(p => p.Role).Select(g => $"{g.Key}={g.Count()}")));
            span?.SetTag("intake.prepare.image_bytes", run.Prepared.Parts.Where(p => p.Role == PartRole.PageImage).Sum(p => (long)(p.Data?.Length ?? 0)));
        }
        run.Lap("prepare");
        if (run.Risks.Count > 0)
        {
            Activity.Current?.SetTag("intake.risk.count", run.Risks.Count);
            Activity.Current?.SetTag("intake.risk.kinds", string.Join(",", run.Risks.Select(r => $"{r.Kind}:{r.Severity}").Distinct()));
        }
    }

    private async Task ScanAsync(Stream document, long bytes, CancellationToken ct)
    {
        using var span = Telemetry.Source.StartActivity("intake.scan");
        var started = Stopwatch.GetTimestamp();
        var scan = await scanner.ScanAsync(document, ct);
        var result = scan.Outcome.ToString().ToLowerInvariant();
        metrics.RecordScan(result, Stopwatch.GetElapsedTime(started));
        span?.SetTag("intake.scan.result", result);
        switch (scan.Outcome)
        {
            case ScanOutcome.Infected:
                span?.SetTag("intake.scan.signature", scan.Signature);
                logger.LogWarning("Malware found in a {Bytes}-byte upload: {Signature}", bytes, scan.Signature);
                // The signature stays in the log and the trace; the response only says the document was refused.
                throw new IntakeFailureException(FailureKind.Malicious, "document rejected: malware detected",
                    inner: new InvalidDataException($"scanner signature {scan.Signature}"));
            case ScanOutcome.Unavailable when security.Value.Malware.FailClosed:
                span?.SetStatus(ActivityStatusCode.Error, "no verdict");
                throw new MalwareScannerUnavailableException(scan.Error ?? "the scanner gave no verdict");
            case ScanOutcome.Unavailable:
                logger.LogWarning("Accepting a {Bytes}-byte upload without a malware verdict (FailMode is open): {Reason}", bytes, scan.Error);
                break;
        }
    }
}

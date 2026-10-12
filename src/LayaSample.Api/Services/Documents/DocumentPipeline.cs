using System.Diagnostics;
using LayaSample.Api.Models;
using LayaSample.Api.Services.Security;
using LayaSample.Rendering;
using Microsoft.Extensions.Options;

namespace LayaSample.Api.Services.Documents;

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
        Activity.Current?.SetTag("laya.file.bytes", file.Length);
        Activity.Current?.SetTag("laya.file.content_type", file.ContentType);

        // Before any parser sees the file: an infected upload is turned away without being read by PdfPig, the Office
        // libraries or the renderer.
        if (scanner.Enabled)
        {
            await ScanAsync(stream, file.Length, ct);
            run.Lap("scan");
        }

        using (var span = Telemetry.Source.StartActivity("laya.classify"))
        {
            run.Classification = await classifier.ClassifyAsync(stream, ct);
            span?.SetTag("laya.document.kind", run.Classification.Kind.ToString());
            span?.SetTag("laya.document.pages", run.Classification.PageCount);
            span?.SetTag("laya.document.strategy", run.Classification.Strategy.ToString());
            span?.SetTag("laya.document.route", run.Classification.Route);
            span?.SetTag("laya.document.is_xfa", run.Classification.IsXfa);
            span?.SetTag("laya.document.page_contents", string.Join(",", run.Classification.Pages.GroupBy(p => p.Content).Select(g => $"{g.Key}={g.Count()}")));
            metrics.RecordUpload(run.Classification.Kind.ToString(), file.Length, run.Classification.PageCount);
            // Before anything is rendered or read: a file with active content or hidden text is turned away here (with
            // OnFinding=reject) without going to the renderer.
            run.Risks.AddRange(riskScanner.CheckStructure(run.Classification));
        }
        run.Lap("classify");
        using (var span = Telemetry.Source.StartActivity("laya.prepare"))
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
            span?.SetTag("laya.prepare.parts", run.Prepared.Parts.Count);
            span?.SetTag("laya.prepare.part_roles", string.Join(",", run.Prepared.Parts.GroupBy(p => p.Role).Select(g => $"{g.Key}={g.Count()}")));
            span?.SetTag("laya.prepare.image_bytes", run.Prepared.Parts.Where(p => p.Role == PartRole.PageImage).Sum(p => (long)(p.Data?.Length ?? 0)));
        }
        run.Lap("prepare");
        if (run.Risks.Count > 0)
        {
            Activity.Current?.SetTag("laya.risk.count", run.Risks.Count);
            Activity.Current?.SetTag("laya.risk.kinds", string.Join(",", run.Risks.Select(r => $"{r.Kind}:{r.Severity}").Distinct()));
        }
    }

    private async Task ScanAsync(Stream document, long bytes, CancellationToken ct)
    {
        using var span = Telemetry.Source.StartActivity("laya.scan");
        var started = Stopwatch.GetTimestamp();
        var scan = await scanner.ScanAsync(document, ct);
        var result = scan.Outcome.ToString().ToLowerInvariant();
        metrics.RecordScan(result, Stopwatch.GetElapsedTime(started));
        span?.SetTag("laya.scan.result", result);
        switch (scan.Outcome)
        {
            case ScanOutcome.Infected:
                span?.SetTag("laya.scan.signature", scan.Signature);
                logger.LogWarning("Malware found in a {Bytes}-byte upload: {Signature}", bytes, scan.Signature);
                // The signature stays in the log and the trace; the response only says the document was refused.
                throw new DocumentException("document rejected: malware detected", StatusCodes.Status422UnprocessableEntity,
                    new InvalidDataException($"scanner signature {scan.Signature}"));
            case ScanOutcome.Unavailable when security.Value.Malware.FailClosed:
                span?.SetStatus(ActivityStatusCode.Error, "no verdict");
                throw new MalwareScannerUnavailableException(scan.Error ?? "the scanner gave no verdict");
            case ScanOutcome.Unavailable:
                logger.LogWarning("Accepting a {Bytes}-byte upload without a malware verdict (FailMode is open): {Reason}", bytes, scan.Error);
                break;
        }
    }
}

/// <summary>Maps failures caused by the uploaded document or the rendering services to problem responses.</summary>
public sealed class DocumentProblems(ILogger<DocumentProblems> logger, DocumentMetrics metrics)
{
    public static bool Handles(Exception ex) =>
        ex is DocumentException or OcrUnavailableException or RendererUnavailableException or RenderingFailedException or MalwareScannerUnavailableException;

    public IResult ToResult(Exception ex, long bytes, DocumentClassification? classification, HttpContext httpContext)
    {
        var kind = classification?.Kind.ToString() ?? "unclassified";
        var outcome = ex switch
        {
            DocumentException => "rejected",
            OcrUnavailableException => "ocr_unavailable",
            RendererUnavailableException => "renderer_unavailable",
            MalwareScannerUnavailableException => "scanner_unavailable",
            _ => "render_failed"
        };
        Telemetry.TagRequest(httpContext, "laya.outcome", outcome);
        Telemetry.TagRequest(httpContext, "laya.document.kind", kind);
        Activity.Current?.RecordFailure(ex, outcome);
        switch (ex)
        {
            case DocumentException doc:
                // The inner exception says why a library rejected the content; the client only gets the summary.
                logger.LogInformation(doc.InnerException, "Rejected {Bytes}-byte {Kind} document with {Status}: {Reason}",
                    bytes, kind, doc.StatusCode, doc.Message);
                metrics.RecordDocument(kind, "none", doc.StatusCode);
                return Problem(doc.StatusCode, doc.Message);
            case OcrUnavailableException:
                logger.LogError(ex, "OCR is unavailable");
                metrics.RecordDocument(kind, "none", StatusCodes.Status503ServiceUnavailable);
                return Problem(StatusCodes.Status503ServiceUnavailable, "OCR is unavailable; retry later");
            case MalwareScannerUnavailableException:
                logger.LogWarning(ex, "Malware scanner is unavailable; refusing the upload");
                metrics.RecordDocument(kind, "none", StatusCodes.Status503ServiceUnavailable);
                httpContext.Response.Headers.RetryAfter = "10";
                return Problem(StatusCodes.Status503ServiceUnavailable, "document scanning is unavailable; retry later");
            case RendererUnavailableException:
                logger.LogWarning(ex, "Renderer is unavailable");
                metrics.RecordDocument(kind, "none", StatusCodes.Status503ServiceUnavailable);
                httpContext.Response.Headers.RetryAfter = "10";
                return Problem(StatusCodes.Status503ServiceUnavailable, "document rendering is unavailable; retry later");
            default:
                // Most likely this document crashed the renderer; retrying it would crash it again.
                logger.LogError(ex, "Renderer failed on a {Bytes}-byte {Kind} document", bytes, kind);
                metrics.RecordDocument(kind, "none", StatusCodes.Status502BadGateway);
                return Problem(StatusCodes.Status502BadGateway, "document could not be rendered");
        }
    }

    public static IResult Problem(int status, string detail) => Results.Problem(detail: detail, statusCode: status);
}

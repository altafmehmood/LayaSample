using System.Diagnostics;
using LayaSample.Api.Models;
using LayaSample.Rendering;

namespace LayaSample.Api.Services.Documents;

/// <summary>One upload's trip through the pipeline: stage timings plus what each stage produced so far.</summary>
public sealed class DocumentRun(DocumentMetrics metrics)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _stage = Stopwatch.GetTimestamp();

    public DocumentClassification? Classification { get; internal set; }
    public PreparedDocument? Prepared { get; internal set; }
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

    public void Lap(string name)
    {
        metrics.RecordStage(name, Stopwatch.GetElapsedTime(_stage));
        _stage = Stopwatch.GetTimestamp();
    }
}

/// <summary>Buffer the upload, classify it and prepare it. Shared by every endpoint that works on an uploaded document.</summary>
public sealed class DocumentPipeline(IDocumentClassifier classifier, IDocumentPreparer preparer, DocumentMetrics metrics)
{
    public DocumentRun NewRun() => new(metrics);

    public async Task PrepareAsync(IFormFile file, DocumentRun run, CancellationToken ct)
    {
        // Buffered once: PDF, ZIP and TIFF need random access. The buffer is sized exactly so classify and prepare
        // share it rather than each taking a copy.
        await using var stream = new MemoryStream((int)file.Length);
        using (var span = Telemetry.Source.StartActivity("laya.buffer"))
        {
            span?.SetTag("laya.file.bytes", file.Length);
            span?.SetTag("laya.file.content_type", file.ContentType);
            await file.CopyToAsync(stream, ct);
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
            span?.SetTag("laya.prepare.parts", run.Prepared.Parts.Count);
            span?.SetTag("laya.prepare.part_roles", string.Join(",", run.Prepared.Parts.GroupBy(p => p.Role).Select(g => $"{g.Key}={g.Count()}")));
            span?.SetTag("laya.prepare.image_bytes", run.Prepared.Parts.Where(p => p.Role == PartRole.PageImage).Sum(p => (long)(p.Data?.Length ?? 0)));
        }
        run.Lap("prepare");
    }
}

/// <summary>Maps failures caused by the uploaded document or the rendering services to problem responses.</summary>
public sealed class DocumentProblems(ILogger<DocumentProblems> logger, DocumentMetrics metrics)
{
    public static bool Handles(Exception ex) =>
        ex is DocumentException or OcrUnavailableException or RendererUnavailableException or RenderingFailedException;

    public IResult ToResult(Exception ex, long bytes, DocumentClassification? classification, HttpContext httpContext)
    {
        var kind = classification?.Kind.ToString() ?? "unclassified";
        var outcome = ex switch
        {
            DocumentException => "rejected",
            OcrUnavailableException => "ocr_unavailable",
            RendererUnavailableException => "renderer_unavailable",
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

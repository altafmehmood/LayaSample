using System.Diagnostics;
using DocIntake.Api.Services;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using DocIntake.Api.Services.Usage;
using DocIntake.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace DocIntake.Api.Endpoints;

/// <summary>
/// The document endpoints: analyse an upload (classify, prepare, route) and extract data from it. The handlers do the work and
/// throw when it fails; the upload filter checks the file first and the failure handler answers (<see cref="ProblemMapping"/>).
/// </summary>
internal static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app, DocumentGatewayOptions options)
    {
        // Room for the multipart framing around the file, so an allowed file is never cut off by the body limit.
        var maxBodyBytes = options.MaxFileBytes + 64 * 1024;
        var timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);

        app.MapPost("/api/documents/analyze", Analyze)
            .AddEndpointFilter(new UploadValidationFilter("analyze"))
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data")
            .WithName("AnalyzeDocument")
            .WithSummary("Classify an uploaded PDF, Excel, Word or image document, prepare it for an AI agent and route it")
            .WithDescription("The preparation strategy (page images, markdown, page images with OCR text, structured data) is chosen per page "
                + "from the content and reported in classification.strategy and classification.pages[].strategy. "
                + "includeData=false leaves page images out of the response (parts keep their metadata and text).")
            .Produces<AnalyzeDocumentResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .WithMetadata(new RequestSizeLimitAttribute(maxBodyBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxBodyBytes })
            .WithRequestTimeout(timeout)
            .RequireRateLimiting(RateLimitPolicies.Documents);

        app.MapPost("/api/documents/extract", Extract)
            // In this order: ask the service first, so a deployment without a key answers 503 before the upload is validated.
            .AddEndpointFilter(new RequireExtractionFilter())
            .AddEndpointFilter(new UploadValidationFilter("extract"))
            .DisableAntiforgery()
            .RequireApiKey()
            .WithName("ExtractDocument")
            .WithSummary("Extract borrower, lender, collateral and UCC details from a lending document")
            .WithDescription("Reads a loan application, UCC-1, UCC-3 or loan and security agreement with an OpenAI model (page images when the "
                + "document is scanned or laid out, text otherwise). Returns the parties and the document's fields, each with its page and a short quote; "
                + "every box on the UCC-1 and UCC-3 forms is its own attribute, checkboxes are true, false or null when they cannot be told, "
                + "and anything else printed on the document comes back in otherFields as name/value pairs. The document's pages leave this service "
                + "and go to the model provider.")
            .Produces<ExtractDocumentResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .WithMetadata(new RequestSizeLimitAttribute(maxBodyBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = maxBodyBytes })
            .WithRequestTimeout(timeout)
            .RequireRateLimiting(RateLimitPolicies.Extraction);
    }

    private static async Task<IResult> Analyze(
        IFormFile? file,
        bool? dispatch,
        bool? includeData,
        DocumentPipeline pipeline,
        IDocumentRouter router,
        DocumentMetrics metrics,
        ILogger<Program> logger,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var run = httpContext.GetDocumentRequest().Run;
        await pipeline.PrepareAsync(file!, run, ct);
        var classification = run.Classification!;
        var prepared = run.Prepared!;
        TagDocument(httpContext, classification, prepared);
        var agent = dispatch == false ? null : await router.RouteAsync(classification, prepared, ct);
        run.Lap("route");
        Telemetry.TagRequest(httpContext, "intake.outcome", "ok");

        // Content only, never file names or text: both can carry personal data.
        logger.LogInformation("Analysed {Bytes}-byte {Kind} document: {Strategy}, {Pages} page(s), {Parts} part(s) in {ElapsedMs:F0} ms",
            file!.Length, classification.Kind, prepared.Strategy, classification.PageCount, prepared.Parts.Count,
            run.Elapsed.TotalMilliseconds);
        metrics.RecordDocument(classification.Kind.ToString(), prepared.Strategy.ToString(), StatusCodes.Status200OK);

        var parts = prepared.Parts
            .Select(p => new PreparedPartDto(p.Role, p.MediaType, p.Page, p.Source, p.Text, includeData == false ? null : p.Data))
            .ToList();
        return Results.Ok(new AnalyzeDocumentResponse(file.FileName, classification, parts, agent, run.Risks));
    }

    private static async Task<IResult> Extract(
        IFormFile? file,
        DocumentPipeline pipeline,
        IDocumentExtractor extractor,
        DocumentMetrics metrics,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var run = httpContext.GetDocumentRequest().Run;
        await pipeline.PrepareAsync(file!, run, ct);
        TagDocument(httpContext, run.Classification!, run.Prepared!);
        // Who asked and what was uploaded, so each model call lands in the usage history (the key as a fingerprint only).
        var usageContext = new UsageContext(httpContext.GetCaller(), UsageFileName.Clean(file!.FileName), file.Length,
            run.Classification!.Kind.ToString(), run.Classification.PageCount, Activity.Current?.TraceId.ToString());
        var response = await extractor.ExtractAsync(file.FileName, run.Classification!, run.Prepared!, ct, usageContext);
        // Findings about the file (structure and text) and about the answer, in one list.
        response = response with { Risks = [.. run.Risks, .. response.Risks ?? []] };
        run.Lap("extract");
        Telemetry.TagRequest(httpContext, "intake.outcome", "ok");
        Telemetry.TagRequest(httpContext, "intake.extraction.document_type", response.DocumentType.ToString());
        metrics.RecordDocument(run.Classification!.Kind.ToString(), run.Prepared!.Strategy.ToString(), StatusCodes.Status200OK);
        return Results.Ok(response);
    }

    // What the upload turned out to be, on the request's own span.
    private static void TagDocument(HttpContext context, DocumentClassification classification, PreparedDocument prepared)
    {
        Telemetry.TagRequest(context, "intake.document.kind", classification.Kind.ToString());
        Telemetry.TagRequest(context, "intake.document.strategy", prepared.Strategy.ToString());
        Telemetry.TagRequest(context, "intake.document.pages", classification.PageCount);
        Telemetry.TagRequest(context, "intake.document.parts", prepared.Parts.Count);
    }
}

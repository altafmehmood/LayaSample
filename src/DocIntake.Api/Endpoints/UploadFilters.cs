using DocIntake.Api.Services;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using Microsoft.Extensions.Options;

namespace DocIntake.Api.Endpoints;

/// <summary>
/// The checks every upload endpoint makes before it does any work: there is a file (400), it is not larger than allowed (413).
/// It then starts the request's <see cref="DocumentRequest"/> (size and run, for the failure handler), tags the span with the
/// file's size and records how long the request waited for its slot. Throwing from here or from the handler needs no catch.
/// </summary>
internal sealed class UploadValidationFilter(string endpoint) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        var maxBytes = services.GetRequiredService<IOptions<DocumentGatewayOptions>>().Value.MaxFileBytes;

        var file = context.Arguments.OfType<IFormFile>().FirstOrDefault();
        if (file is null || file.Length == 0)
            return ProblemMapping.Problem(StatusCodes.Status400BadRequest, "file is required");
        if (file.Length > maxBytes)
            return ProblemMapping.Problem(StatusCodes.Status413PayloadTooLarge, $"file must be at most {maxBytes} bytes");

        Telemetry.TagRequest(http, "intake.file.bytes", file.Length);
        var queueWait = Telemetry.RequestAge(http);
        Telemetry.TagRequest(http, "intake.request.queue_wait_ms", (long)queueWait.TotalMilliseconds);
        services.GetRequiredService<DocumentMetrics>().RecordQueueWait(endpoint, queueWait);
        http.Items[DocumentRequest.ItemKey] = new DocumentRequest(file.Length, services.GetRequiredService<DocumentPipeline>().NewRun());
        return await next(context);
    }
}

/// <summary>
/// Asks the extraction service whether it can extract, before the upload is validated, read, scanned or prepared: without a key
/// the answer is 503 at once, and an unreachable service is the same 503 with a retry hint (thrown, and answered by the
/// failure handler).
/// </summary>
internal sealed class RequireExtractionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var extractor = context.HttpContext.RequestServices.GetRequiredService<IDocumentExtractor>();
        if (!await extractor.IsConfiguredAsync(context.HttpContext.RequestAborted))
            return ProblemMapping.Problem(StatusCodes.Status503ServiceUnavailable, "extraction is not configured");
        return await next(context);
    }
}

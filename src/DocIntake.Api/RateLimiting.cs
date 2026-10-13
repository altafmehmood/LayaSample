using System.Threading.RateLimiting;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using Microsoft.AspNetCore.RateLimiting;

namespace DocIntake.Api;

/// <summary>The names of the gateway's rate-limit policies (an endpoint names its policy; the metric says which pool ran dry).</summary>
internal static class RateLimitPolicies
{
    public const string Documents = "documents";
    public const string Extraction = "extraction";
}

internal static class RateLimiting
{
    public static IServiceCollection AddGatewayRateLimiting(this IServiceCollection services, DocumentGatewayOptions documents, ExtractionGatewayOptions extraction)
    {
        services.AddRequestTimeouts();
        return services.AddRateLimiter(o =>
        {
            // Analysis sends a document to a service that parses it; bound how many are in flight instead of flooding it.
            o.AddConcurrencyLimiter(RateLimitPolicies.Documents, l =>
            {
                l.PermitLimit = documents.EffectiveMaxConcurrentAnalyses;
                l.QueueLimit = documents.MaxQueuedAnalyses;
                l.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
            // Extraction mostly waits on the model provider, so it gets its own slots rather than competing with analysis.
            o.AddConcurrencyLimiter(RateLimitPolicies.Extraction, l =>
            {
                l.PermitLimit = extraction.MaxConcurrentExtractions;
                l.QueueLimit = extraction.MaxQueuedExtractions;
                l.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
            o.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
            o.OnRejected = (context, ct) =>
            {
                // The policy is the endpoint's metadata name; the metric says which pool ran dry.
                var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "unknown";
                var services = context.HttpContext.RequestServices;
                services.GetRequiredService<DocumentMetrics>().RecordRateLimited(policy);
                Telemetry.TagRequest(context.HttpContext, "intake.outcome", "rate_limited");
                Telemetry.TagRequest(context.HttpContext, "intake.ratelimit.policy", policy);
                services.GetRequiredService<ILoggerFactory>().CreateLogger("DocIntake.RateLimiting")
                    .LogWarning("Rejected {Path}: every {Policy} slot and queue place is taken (waited {WaitedMs:F0} ms)",
                        context.HttpContext.Request.Path.Value, policy, Telemetry.RequestAge(context.HttpContext).TotalMilliseconds);
                context.HttpContext.Response.Headers.RetryAfter = "10";
                return context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = { Status = StatusCodes.Status503ServiceUnavailable, Detail = "too many documents are being analysed; retry later" }
                });
            };
        });
    }
}

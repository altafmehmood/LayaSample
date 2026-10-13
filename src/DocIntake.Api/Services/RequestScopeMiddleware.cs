using DocIntake.Api.Services.Usage;
using Microsoft.AspNetCore.Http.Metadata;

namespace DocIntake.Api.Services;

/// <summary>
/// Opens a logging scope for the request, so every line written while it runs (including by the services it calls into on
/// this side) carries the endpoint and who called, under the same names as the span attributes. It runs after the API-key
/// middleware, which is what finds out who is calling, and puts the same two facts on the request's span. The caller is a
/// short hash of the key, never the key, and is not passed to the other services.
/// </summary>
public sealed class RequestScopeMiddleware(RequestDelegate next, ILogger<RequestScopeMiddleware> logger)
{
    public Task InvokeAsync(HttpContext context)
    {
        var caller = context.GetCaller();
        var endpoint = context.GetEndpoint()?.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName ?? "unmatched";
        Telemetry.TagRequest(context, "intake.endpoint", endpoint);
        Telemetry.TagRequest(context, "intake.caller.fingerprint", caller.Fingerprint);
        Telemetry.TagRequest(context, "intake.caller.admin", caller.IsAdmin);
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["intake.endpoint"] = endpoint,
            ["intake.caller.fingerprint"] = caller.Fingerprint,
            ["intake.caller.admin"] = caller.IsAdmin
        });
        return next(context);
    }
}

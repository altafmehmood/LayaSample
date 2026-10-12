using DocIntake.Contracts;
using Microsoft.AspNetCore.Http;

namespace DocIntake.ServiceDefaults;

/// <summary>
/// How a service answers a failure over HTTP. The status table is here, once, for every service that answers (the gateway's
/// <c>ProblemMapping</c> uses it too), and a service names the <see cref="FailureKind"/> in the answer so the caller can
/// raise the same failure on its side.
/// </summary>
public static class ServiceProblems
{
    /// <summary>The problem-details extension that carries the <see cref="FailureKind"/>.</summary>
    public const string KindExtension = "kind";

    public static int StatusOf(FailureKind kind) => kind switch
    {
        FailureKind.Unsupported => StatusCodes.Status415UnsupportedMediaType,
        FailureKind.Unreadable or FailureKind.OverLimit or FailureKind.Malicious or FailureKind.PolicyRejected
            or FailureKind.Declined => StatusCodes.Status422UnprocessableEntity,
        FailureKind.Busy or FailureKind.Misconfigured => StatusCodes.Status503ServiceUnavailable,
        FailureKind.Timeout => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status502BadGateway
    };

    /// <summary>The answer for a failure a service knows how to report, or null for anything else (which stays a 500).</summary>
    public static IResult? FromException(Exception ex) => ex switch
    {
        IntakeFailureException f => Results.Problem(detail: f.Message, statusCode: StatusOf(f.Kind),
            extensions: new Dictionary<string, object?> { [KindExtension] = f.Kind.ToString() }),
        OcrUnavailableException o => Results.Problem(detail: o.Message, statusCode: StatusCodes.Status503ServiceUnavailable,
            type: OcrUnavailableException.ProblemType),
        RenderingFailedException r => Results.Problem(detail: r.Message, statusCode: StatusCodes.Status502BadGateway),
        _ => null
    };
}

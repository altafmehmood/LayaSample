using DocIntake.Api.Services;
using DocIntake.Api.Services.Decisions;
using DocIntake.Contracts;

namespace DocIntake.Api.Endpoints;

/// <summary>Feedback analysis: validated here, answered by the decisions service (Laya).</summary>
internal static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/feedback/analyze", Analyze)
            .WithName("AnalyzeFeedback")
            .WithSummary("Analyze hotel guest feedback for dissatisfaction")
            .Produces<AnalyzeResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> Analyze(AnalyzeRequest req, DecisionsServiceClient decisions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return ProblemMapping.Problem(StatusCodes.Status400BadRequest, "text is required");
        if (req.Text.Length > FeedbackLimits.MaxTextLength)
            return ProblemMapping.Problem(StatusCodes.Status400BadRequest, $"text must be at most {FeedbackLimits.MaxTextLength} characters");
        if (!decisions.IsConfigured)
            return ProblemMapping.Problem(StatusCodes.Status503ServiceUnavailable, "feedback analysis is not configured");

        // The service's own "model is still loading" or "model failed to load" is thrown and answered as 503, with its text.
        return Results.Ok(await decisions.AnalyzeAsync(req.Text, ct));
    }
}

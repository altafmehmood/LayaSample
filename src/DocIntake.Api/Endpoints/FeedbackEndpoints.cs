using DocIntake.Api.Services;
using DocIntake.Contracts;

namespace DocIntake.Api.Endpoints;

/// <summary>
/// Feedback analysis: validated here. The local model (Laya) that answered it was removed; the route stays so callers keep
/// their contract until the OpenAI-based replacement, and answers 503 meanwhile.
/// </summary>
internal static class FeedbackEndpoints
{
    public static void MapFeedbackEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/feedback/analyze", Analyze)
            .WithName("AnalyzeFeedback")
            .WithSummary("Analyze hotel guest feedback for dissatisfaction (not available)")
            .Produces<AnalyzeResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    private static IResult Analyze(AnalyzeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return ProblemMapping.Problem(StatusCodes.Status400BadRequest, "text is required");
        if (req.Text.Length > FeedbackLimits.MaxTextLength)
            return ProblemMapping.Problem(StatusCodes.Status400BadRequest, $"text must be at most {FeedbackLimits.MaxTextLength} characters");

        return ProblemMapping.Problem(StatusCodes.Status503ServiceUnavailable, "feedback analysis is not available");
    }
}

using ElBruno.LocalLLMs.Decisions;
using LayaSample.Api.Models;

namespace LayaSample.Api.Services;

public interface IDissatisfactionAnalyzer
{
    Task<AnalyzeResponse> AnalyzeAsync(string feedback, CancellationToken ct = default);
}

public sealed class DissatisfactionAnalyzer(IDecisionClient client) : IDissatisfactionAnalyzer
{
    private static readonly string[] RatingLevels =
        ["not at all dissatisfied", "slightly dissatisfied", "moderately dissatisfied", "very dissatisfied", "extremely dissatisfied"];

    private static readonly string[] Drivers =
        ["Room", "Cleanliness", "Staff", "Noise", "Food", "Price", "Facilities", "Other"];

    public async Task<AnalyzeResponse> AnalyzeAsync(string feedback, CancellationToken ct = default)
    {
        var request = new DecisionRequest(feedback)
            .Ask("dissatisfied", "The guest is dissatisfied with their hotel stay.")
            .Score("rating", RatingLevels, "How dissatisfied is the guest with their hotel stay?")
            .Choose("driver", Drivers, "What is the main cause of the guest's dissatisfaction?");

        var result = await client.EvaluateAsync(request, ct);

        var asked = result.Probability("dissatisfied");
        var score = result.Score("rating");
        var driver = result.Choice("driver");

        var p = asked.Probability;
        var level = p switch
        {
            < 0.25 => "None",
            < 0.5 => "Mild",
            < 0.8 => "Moderate",
            _ => "Severe"
        };

        return new AnalyzeResponse(
            Dissatisfied: asked.IsTrue,
            DissatisfactionScore: Math.Round(p, 4),
            Rating: score.MostLikelyLevel + 1,
            Level: level,
            PrimaryDriver: asked.IsTrue ? driver.ChoiceOrNull(0.3) : null,
            Confidence: Math.Round(Math.Abs(p - 0.5) * 2, 4));
    }
}

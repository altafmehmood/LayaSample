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
        return Map(result.Probability("dissatisfied"), result.Score("rating"), result.Choice("driver"));
    }

    /// <summary>
    /// The level follows <see cref="AnalyzeResponse.Dissatisfied"/> (which uses the configured decision threshold) and
    /// the rating, so the two can never disagree: None when not dissatisfied, otherwise how strongly.
    /// </summary>
    public static AnalyzeResponse Map(ProbabilityResult asked, ScoreResult rating, ChoiceResult driver)
    {
        var p = asked.Probability;
        var level = !asked.IsTrue ? "None" : rating.MostLikelyLevel switch
        {
            <= 1 => "Mild",       // not at all / slightly dissatisfied
            2 => "Moderate",
            _ => "Severe"         // very / extremely dissatisfied
        };

        return new AnalyzeResponse(
            Dissatisfied: asked.IsTrue,
            DissatisfactionScore: Math.Round(p, 4),
            Rating: rating.MostLikelyLevel + 1,
            Level: level,
            PrimaryDriver: asked.IsTrue ? driver.ChoiceOrNull(0.3) : null,
            Confidence: Math.Round(Math.Abs(p - 0.5) * 2, 4));
    }
}

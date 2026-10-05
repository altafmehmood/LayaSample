using ElBruno.LocalLLMs.Decisions;
using LayaSample.Api.Services;

namespace LayaSample.Tests;

public class DissatisfactionAnalyzerTests
{
    private static readonly string[] Legend =
        ["not at all dissatisfied", "slightly dissatisfied", "moderately dissatisfied", "very dissatisfied", "extremely dissatisfied"];

    private static ScoreResult Rating(int level) =>
        new(level, Enumerable.Range(0, 5).ToDictionary(i => i, i => i == level ? 0.8 : 0.05), Legend, 0.8, false);

    private static ChoiceResult Driver(string choice, double confidence) =>
        new(choice, new Dictionary<string, double> { [choice] = confidence }, confidence, false);

    [Fact]
    public void Below_the_threshold_is_not_dissatisfied_whatever_the_rating()
    {
        // p = 0.4 under a 0.5 threshold used to come out as Level "Mild" next to Dissatisfied = false.
        var result = DissatisfactionAnalyzer.Map(new ProbabilityResult(0.4, 0.5, false), Rating(1), Driver("Room", 0.9));

        Assert.Equal((false, "None", (string?)null), (result.Dissatisfied, result.Level, result.PrimaryDriver));
        Assert.Equal(2, result.Rating);
    }

    [Theory]
    [InlineData(0, "Mild")]
    [InlineData(1, "Mild")]
    [InlineData(2, "Moderate")]
    [InlineData(3, "Severe")]
    [InlineData(4, "Severe")]
    public void Level_of_a_dissatisfied_guest_follows_the_rating(int ratingLevel, string level)
    {
        var result = DissatisfactionAnalyzer.Map(new ProbabilityResult(0.9, 0.5, false), Rating(ratingLevel), Driver("Staff", 0.9));

        Assert.Equal((true, level, ratingLevel + 1), (result.Dissatisfied, result.Level, result.Rating));
    }

    [Fact]
    public void Driver_is_omitted_when_the_model_is_unsure()
    {
        var result = DissatisfactionAnalyzer.Map(new ProbabilityResult(0.9, 0.5, false), Rating(3), Driver("Noise", 0.1));

        Assert.Null(result.PrimaryDriver);
    }

    [Fact]
    public void Score_and_confidence_are_rounded_probabilities()
    {
        var result = DissatisfactionAnalyzer.Map(new ProbabilityResult(0.912345, 0.5, false), Rating(3), Driver("Food", 0.9));

        Assert.Equal((0.9123, 0.8247, "Food"), (result.DissatisfactionScore, result.Confidence, result.PrimaryDriver));
    }
}

namespace DocIntake.Contracts;

public sealed record AnalyzeRequest(string? Text);

/// <summary>What feedback analysis accepts. The gateway checks it at the edge.</summary>
public static class FeedbackLimits
{
    public const int MaxTextLength = 4000;
}

public sealed record AnalyzeResponse(
    bool Dissatisfied,
    double DissatisfactionScore,
    int Rating,
    string Level,
    string? PrimaryDriver,
    double Confidence);

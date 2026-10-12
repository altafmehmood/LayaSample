namespace DocIntake.Contracts;

public sealed record AnalyzeRequest(string? Text);

public sealed record AnalyzeResponse(
    bool Dissatisfied,
    double DissatisfactionScore,
    int Rating,
    string Level,
    string? PrimaryDriver,
    double Confidence);

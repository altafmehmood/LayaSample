namespace DocIntake.Contracts;

/// <summary>Where the scan service answers. The document goes in the request body, as raw bytes.</summary>
public static class ScanRoutes
{
    public const string Scan = "scan";
    public const string Ready = "health/ready";
}

/// <summary>The scan service's verdict on one document. <see cref="Outcome"/> is one of <see cref="ScanOutcomes"/>.</summary>
/// <param name="Signature">What the scanner called the malware, for the log and the trace, never the caller's response.</param>
/// <param name="Error">Why there is no verdict, for the log.</param>
public sealed record ScanResponse(string Outcome, string? Signature = null, string? Error = null);

public static class ScanOutcomes
{
    public const string Clean = "clean";
    public const string Infected = "infected";
    /// <summary>No verdict: the daemon could not be reached, timed out, or refused the document (for example, too large).</summary>
    public const string Unavailable = "unavailable";
}

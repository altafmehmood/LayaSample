namespace DocIntake.Contracts;

/// <summary>What the gateway sends the extraction service: the classification and the prepared document.</summary>
/// <remarks>The file's name and the caller are not sent: the service needs neither to read the document.</remarks>
public sealed record ExtractRequest(DocumentClassification Classification, PreparedDocument Document);

/// <summary>
/// One model call, as the extraction service saw it. The gateway turns each into a usage record, adding who asked and what
/// was uploaded. <see cref="Usage"/> is null when the call failed before the provider answered.
/// </summary>
/// <param name="At">When the call ended.</param>
/// <param name="Images">Page images sent with the call.</param>
/// <param name="PriceKey">The model and the date of the price list used, or null when the model has no configured price.</param>
/// <param name="Outcome">"ok", "timeout", "cancelled" or "http_" and the status the failure maps to.</param>
public sealed record ModelCall(
    DateTimeOffset At, string Agent, string Model, int Attempt, int Images, ExtractionUsage? Usage, string? PriceKey, string Outcome,
    long DurationMs);

/// <summary>
/// What the extraction service answers. The findings about the answer are reported here, never acted on: whether an
/// instruction-like answer refuses the request is the gateway's policy.
/// </summary>
public sealed record ExtractAnswer(
    LendingDocumentType DocumentType,
    string DetectedBy,
    string Agent,
    string Model,
    ExtractionResult Extraction,
    ExtractionUsage Usage,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RiskFinding> AnswerRisks,
    IReadOnlyList<ModelCall> Calls);

/// <summary>Whether the extraction service can extract: it has a model provider key.</summary>
public sealed record ExtractionStatus(bool Configured);

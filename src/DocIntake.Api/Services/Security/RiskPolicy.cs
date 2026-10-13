using DocIntake.Contracts;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Usage;
using Microsoft.Extensions.Options;

namespace DocIntake.Api.Services.Security;

/// <summary>
/// The gateway's policy for findings, and nothing else: it records them and decides whether they refuse the request. The
/// services only find. Findings come from three places: the file's structure (active content, macros, executable attachments,
/// hidden text; found by the document service while classifying), the text the model will read (instruction-like text, found by
/// the document service while preparing), and the model's answer (instructions repeated from the document; found by the
/// extraction service). While <c>Security:OnFinding</c> is <c>reject</c> a blocking finding refuses the request with 422; with
/// <c>flag</c> every finding is reported and processing goes on.
/// </summary>
public sealed class RiskPolicy(IOptions<SecurityOptions> options, DocumentMetrics metrics, ILogger<RiskPolicy> logger)
{
    private static readonly Dictionary<string, string> KindNames = new()
    {
        [RiskKinds.ActiveContent] = "active content",
        [RiskKinds.ExecutableAttachment] = "an executable attachment",
        [RiskKinds.Macro] = "a macro or embedded object",
        [RiskKinds.HiddenText] = "hidden text",
        [RiskKinds.PromptInjection] = "instructions addressed to an AI model"
    };

    private bool Rejects => options.Value.RejectsFindings;

    /// <summary>The findings about the file's structure, before anything is rendered or read. Throws when they refuse the upload.</summary>
    public IReadOnlyList<RiskFinding> EnforceStructure(IReadOnlyList<RiskFinding> findings)
    {
        Record(findings);
        EnforceDocument(findings);
        return findings;
    }

    /// <summary>The findings about the text the model will read (text layer, OCR, form values, attachments). Throws when they refuse the upload.</summary>
    public IReadOnlyList<RiskFinding> EnforceText(IReadOnlyList<RiskFinding> findings)
    {
        Record(findings);
        EnforceDocument(findings);
        return findings;
    }

    /// <summary>
    /// The findings the extraction service reported about the model's answer: an injected instruction that got through can
    /// be echoed into a field, its quote or the list of other fields. Throws when that refuses the answer.
    /// </summary>
    public IReadOnlyList<RiskFinding> EnforceAnswer(IReadOnlyList<RiskFinding> findings)
    {
        Record(findings);
        if (Rejects && findings.Any(f => f.Severity == RiskSeverity.Block))
        {
            logger.LogWarning("Rejected an extraction whose answer repeated instruction-like text ({Count} finding(s))", findings.Count);
            throw new IntakeFailureException(FailureKind.PolicyRejected,
                "extraction rejected: the answer repeated instruction-like text from the document");
        }
        return findings;
    }

    private void Record(IEnumerable<RiskFinding> findings)
    {
        foreach (var finding in findings)
            metrics.RecordRisk(finding.Kind, finding.Severity.ToString().ToLowerInvariant());
    }

    private void EnforceDocument(IReadOnlyList<RiskFinding> findings)
    {
        var blocking = findings.Where(f => f.Severity == RiskSeverity.Block).ToList();
        if (blocking.Count == 0)
            return;
        // Kinds and counts only: the response and the log never repeat what the document said.
        logger.LogWarning("Upload has {Count} blocking finding(s): {Kinds} ({Mode})", blocking.Count,
            string.Join(", ", blocking.Select(f => f.Kind).Distinct()), Rejects ? "rejected" : "flagged");
        if (!Rejects)
            return;
        var what = string.Join(", ", blocking.Select(f => KindNames.GetValueOrDefault(f.Kind, f.Kind)).Distinct());
        throw new IntakeFailureException(FailureKind.PolicyRejected, $"document rejected: it contains {what}");
    }
}

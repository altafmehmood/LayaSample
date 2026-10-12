using DocIntake.Contracts;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using DocIntake.Api.Services.Usage;
using Microsoft.Extensions.Options;

namespace DocIntake.Api.Services.Security;

/// <summary>
/// Turns findings about an upload into a decision. Findings come from three places: the file's structure (active content,
/// macros, executable attachments, hidden text; found while classifying), the text the model will read (prompt injection),
/// and the model's answer (instructions repeated from the document). While <c>Security:OnFinding</c> is <c>reject</c> a
/// blocking finding refuses the request with 422; with <c>flag</c> every finding is reported and processing goes on.
/// </summary>
public sealed class RiskScanner(IOptions<SecurityOptions> options, DocumentMetrics metrics, ILogger<RiskScanner> logger)
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

    /// <summary>The findings the file's structure gave, before anything is rendered or read. Throws when they refuse the upload.</summary>
    public IReadOnlyList<RiskFinding> CheckStructure(DocumentClassification classification)
    {
        Record(classification.Risks);
        EnforceDocument(classification.Risks);
        return classification.Risks;
    }

    /// <summary>The text the model will read (text layer, OCR, form values, attachments). Throws when it refuses the upload.</summary>
    public IReadOnlyList<RiskFinding> CheckText(PreparedDocument prepared)
    {
        var findings = new List<RiskFinding>();
        foreach (var part in prepared.Parts)
        {
            foreach (var id in InjectionPatterns.Find(part.Text))
            {
                var where = part.Source is null ? "" : $" in the embedded file '{UsageFileName.Clean(part.Source)}'";
                var finding = new RiskFinding(RiskKinds.PromptInjection,
                    InjectionPatterns.IsInformational(id) ? RiskSeverity.Info : RiskSeverity.Block, part.Page, $"instruction-like text ({id}){where}");
                if (!findings.Contains(finding))
                    findings.Add(finding);
            }
        }
        Record(findings);
        EnforceDocument(findings);
        return findings;
    }

    /// <summary>
    /// What the model returned. An injected instruction that got through can be echoed into a field, its quote or the list
    /// of other fields. Throws when that refuses the answer.
    /// </summary>
    public IReadOnlyList<RiskFinding> CheckAnswer(ExtractionResult result)
    {
        var findings = new List<RiskFinding>();
        void Look(string? text, int? page)
        {
            foreach (var id in InjectionPatterns.Find(text))
            {
                var finding = new RiskFinding(RiskKinds.PromptInjection,
                    InjectionPatterns.IsInformational(id) ? RiskSeverity.Info : RiskSeverity.Block, page, $"instruction-like text in the answer ({id})");
                if (!findings.Contains(finding))
                    findings.Add(finding);
            }
        }

        foreach (var field in ExtractionValidator.Fields(result))
        {
            Look(field.Value, field.Page);
            Look(field.Evidence, field.Page);
        }
        foreach (var other in result.OtherFields ?? [])
        {
            Look(other.Name, other.Page);
            Look(other.Value, other.Page);
        }

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

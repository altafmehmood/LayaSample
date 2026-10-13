using DocIntake.Contracts;

namespace DocIntake.DocumentService;

/// <summary>
/// Looks for instruction-like text in what the model will read: the text layer, OCR, form values and attachments. Only
/// finds: whether a finding refuses the upload is the gateway's policy (<c>Security:OnFinding</c>). The detail names the kind
/// of thing and where, and never repeats the document's text.
/// </summary>
public static class TextRisks
{
    public static IReadOnlyList<RiskFinding> Find(PreparedDocument prepared)
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
        return findings;
    }
}

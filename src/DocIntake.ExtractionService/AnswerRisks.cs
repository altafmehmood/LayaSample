using DocIntake.Contracts;

namespace DocIntake.ExtractionService;

/// <summary>
/// Looks for instruction-like text in what the model returned. An injected instruction that got through the prompt can be
/// echoed into a field, its quote or the list of other fields. Only finds: whether a finding refuses the request is the
/// gateway's policy (<c>Security:OnFinding</c>).
/// </summary>
public static class AnswerRisks
{
    public static IReadOnlyList<RiskFinding> Find(ExtractionResult result)
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
        return findings;
    }
}

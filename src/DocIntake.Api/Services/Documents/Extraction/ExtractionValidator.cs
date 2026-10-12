using System.Text;
using DocIntake.Api.Models;

namespace DocIntake.Api.Services.Documents.Extraction;

/// <summary>Checks a model's answer against the document it was given: valid page numbers, quotes that really appear.</summary>
public static class ExtractionValidator
{
    private const double MostlyEmpty = 0.8;

    public static IEnumerable<ExtractedField> Fields(ExtractionResult r)
    {
        foreach (var f in new[] { r.Borrower?.Name, r.Borrower?.Address, r.Borrower?.Ein, r.Lender?.Name, r.Lender?.Address })
            if (f is not null) yield return f;
        foreach (var f in SectionFields(r))
            yield return f;
        foreach (var f in ExtraFields(r))
            yield return f;
    }

    // The optional boxes: often legitimately empty, so they never count towards "the wrong agent ran".
    private static IEnumerable<ExtractedField> ExtraFields(ExtractionResult r)
    {
        ExtractedField?[] fields = [
            r.Ucc1?.DebtorEntityType, r.Ucc1?.DebtorJurisdiction, r.Ucc1?.DebtorOrganizationId, r.Ucc1?.AdditionalDebtorName, r.Ucc1?.AlternativeDesignation,
            r.Ucc1?.FilerReference, r.Ucc1?.ContactName, r.Ucc1?.ContactEmail, r.Ucc1?.AcknowledgmentTo,
            r.Ucc3?.AssigneeName, r.Ucc3?.PartyChangeAffects, r.Ucc3?.PartyChangeType, r.Ucc3?.RecordPartyName, r.Ucc3?.ChangedPartyName,
            r.Ucc3?.CollateralChange, r.Ucc3?.CollateralChangeDescription, r.Ucc3?.AuthorizingPartyName, r.Ucc3?.FilerReference,
            r.Ucc3?.ContactName, r.Ucc3?.ContactEmail, r.Ucc3?.AcknowledgmentTo];
        foreach (var f in fields)
            if (f is not null) yield return f;
    }

    private static IEnumerable<ExtractedFlag> Flags(ExtractionResult r)
    {
        ExtractedFlag?[] flags = [
            r.Ucc1?.RealEstateRecords, r.Ucc1?.SearchReport?.AllDebtors, r.Ucc1?.SearchReport?.Debtor1, r.Ucc1?.SearchReport?.Debtor2,
            r.Ucc3?.RealEstateRecords, r.Ucc3?.Termination, r.Ucc3?.Assignment, r.Ucc3?.Continuation, r.Ucc3?.PartyInformationChange,
            r.Ucc3?.AuthorizedByDebtor];
        foreach (var f in flags)
            if (f is not null) yield return f;
    }

    private static IEnumerable<ExtractedField> SectionFields(ExtractionResult r)
    {
        ExtractedField?[] fields = [
            r.LoanApplication?.ApplicationNumber, r.LoanApplication?.ApplicationDate, r.LoanApplication?.AmountRequested, r.LoanApplication?.TermMonths, r.LoanApplication?.Purpose,
            r.Ucc1?.FileNumber, r.Ucc1?.FilingDate, r.Ucc1?.FilingOffice, r.Ucc1?.Jurisdiction, r.Ucc1?.Collateral,
            r.Ucc3?.InitialFileNumber, r.Ucc3?.FileNumber, r.Ucc3?.FilingDate, r.Ucc3?.FilingOffice, r.Ucc3?.Action,
            r.SecurityAgreement?.AgreementDate, r.SecurityAgreement?.Principal, r.SecurityAgreement?.LoanType, r.SecurityAgreement?.Term, r.SecurityAgreement?.GoverningLaw, r.SecurityAgreement?.Collateral];
        foreach (var f in fields)
            if (f is not null) yield return f;
    }

    private const int MaxOtherFields = 50;

    /// <summary>Drops blank pairs, clears page numbers outside the document and caps the list.</summary>
    public static IReadOnlyList<NameValue> CleanOtherFields(IReadOnlyList<NameValue>? other, int pageCount) =>
        (other ?? [])
            .Where(o => o is not null && !string.IsNullOrWhiteSpace(o.Name) && !string.IsNullOrWhiteSpace(o.Value))
            .Select(o => o.Page is { } p && (p < 1 || p > pageCount) ? o with { Page = null } : o)
            .Take(MaxOtherFields)
            .ToList();

    /// <summary>True when the fields that describe the document (not the parties) are nearly all empty, as when the wrong agent ran.</summary>
    public static bool IsMostlyEmpty(ExtractionResult r)
    {
        var section = SectionFields(r).ToList();
        return section.Count == 0 || section.Count(f => string.IsNullOrWhiteSpace(f.Value)) >= section.Count * MostlyEmpty;
    }

    public static List<string> Validate(ExtractionResult result, PreparedDocument document, int pageCount)
    {
        var warnings = new List<string>();
        var pageText = document.Parts
            .Where(p => p.Page is not null && p.Role is PartRole.PageText or PartRole.OcrText && !string.IsNullOrEmpty(p.Text))
            .GroupBy(p => p.Page!.Value)
            .ToDictionary(g => g.Key, g => Compact(string.Concat(g.Select(p => p.Text))));

        int checkedQuotes = 0, missingQuotes = 0;
        foreach (var f in Fields(result))
        {
            if (f.Page is { } p && (p < 1 || p > pageCount))
                f.Page = null;
            if (f.Page is { } page && !string.IsNullOrWhiteSpace(f.Evidence) && pageText.TryGetValue(page, out var text))
            {
                checkedQuotes++;
                if (!text.Contains(Compact(f.Evidence), StringComparison.Ordinal)) missingQuotes++;
            }
        }
        foreach (var flag in Flags(result))
            if (flag.Page is { } fp && (fp < 1 || fp > pageCount))
                flag.Page = null;
        if (missingQuotes > 0)
            warnings.Add($"{missingQuotes} of {checkedQuotes} quoted values could not be found in the page text; check them against the document");
        return warnings;
    }

    internal static string Compact(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }
}

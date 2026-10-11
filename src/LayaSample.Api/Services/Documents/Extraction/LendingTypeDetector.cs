using System.Text;
using System.Text.RegularExpressions;
using LayaSample.Api.Models;

namespace LayaSample.Api.Services.Documents.Extraction;

/// <summary>
/// Recognises which lending document a prepared document is from its first page, using text that is already
/// extracted (text layer, OCR, form field names), so it costs no model call. Matching ignores case, spacing and
/// punctuation, which OCR on scans and faxes tends to disturb. When several titles appear on the first page (a UCC-1's
/// filer reference can name the loan agreement) the earliest one wins. Returns Other when nothing matches.
/// </summary>
public static partial class LendingTypeDetector
{
    [GeneratedRegex("UCCFINANCINGSTATEMENT(?!AMENDMENT)|FORMUCC1")]
    private static partial Regex Ucc1Title();

    [GeneratedRegex("FINANCINGSTATEMENTAMENDMENT|FORMUCC3")]
    private static partial Regex Ucc3Title();

    public static LendingDocumentType Detect(PreparedDocument document)
    {
        var firstPage = document.Parts.Where(p => p.Page == 1 && p.Role is PartRole.PageText or PartRole.OcrText);
        var text = Compact(string.Concat(firstPage.Select(p => p.Text)));

        var found = new List<(int Index, LendingDocumentType Type)>();
        void Add(int index, LendingDocumentType type)
        {
            if (index >= 0) found.Add((index, type));
        }
        Add(Ucc3Title().Match(text) is { Success: true } m3 ? m3.Index : -1, LendingDocumentType.Ucc3);
        Add(text.IndexOf("LOANANDSECURITYAGREEMENT", StringComparison.Ordinal), LendingDocumentType.LoanSecurityAgreement);
        Add(Ucc1Title().Match(text) is { Success: true } m1 ? m1.Index : -1, LendingDocumentType.Ucc1);
        Add(text.IndexOf("LOANAPPLICATION", StringComparison.Ordinal), LendingDocumentType.LoanApplication);
        if (found.Count > 0)
            return found.OrderBy(f => f.Index).First().Type;

        // Form PDFs carry no printed text for the renderer to read, only field names and values.
        var fields = Compact(string.Concat(document.Parts.Where(p => p.Role == PartRole.StructuredData).Select(p => p.Text)));
        if (fields.Contains("INITIALFILENO"))
            return LendingDocumentType.Ucc3;
        if (fields.Contains("ALTLESSEE") && fields.Contains("SEARCHALL"))
            return LendingDocumentType.Ucc1;
        if (fields.Contains("APPLICANTNAME"))
            return LendingDocumentType.LoanApplication;
        return LendingDocumentType.Other;
    }

    private static string Compact(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }
}

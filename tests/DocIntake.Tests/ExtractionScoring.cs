using System.Globalization;
using System.Text.RegularExpressions;
using DocIntake.Api.Models;

namespace DocIntake.Tests;

internal sealed record Check(string File, string Rendering, string Field, bool Pass, string Expected, string? Actual);

/// <summary>
/// Scores an extraction against the corpus manifest's facts. Every fact key must have a scorer here, so a fact added to
/// the manifest cannot be silently ignored (<see cref="ExtractionScoringTests"/> enforces it).
/// </summary>
internal static partial class ExtractionScoring
{
    private enum Kind
    {
        Name,     // company names: case, punctuation and LLC / Inc. / N.A. noise ignored
        Code,     // file numbers, ids, e-mail: letters and digits only, case ignored
        Date,     // any common date format
        Amount,   // money, currency symbols and separators ignored
        Digits,   // digits only
        Text,     // free text: most of the shorter side's words appear in the other
        Keyword,  // the actual value contains the expected word (Add, None, Termination, DEBTOR ...)
        Flag      // a checkbox
    }

    private sealed record Spec(Kind Kind, Func<ExtractionResult, object?> Get);

    private static Spec F(Kind kind, Func<ExtractionResult, ExtractedField?> get) => new(kind, r => get(r));
    private static Spec Box(Func<ExtractionResult, ExtractedFlag?> get) => new(Kind.Flag, r => get(r));

    private static readonly Dictionary<string, Spec> Common = new()
    {
        ["debtorName"] = F(Kind.Name, r => r.Borrower?.Name),
        ["lender"] = F(Kind.Name, r => r.Lender?.Name),
    };

    private static readonly Dictionary<string, Dictionary<string, Spec>> ByType = new()
    {
        ["loan-application"] = new()
        {
            ["ein"] = F(Kind.Digits, r => r.Borrower?.Ein),
            ["amountRequested"] = F(Kind.Amount, r => r.LoanApplication?.AmountRequested),
            ["termMonths"] = F(Kind.Digits, r => r.LoanApplication?.TermMonths),
            ["applicationDate"] = F(Kind.Date, r => r.LoanApplication?.ApplicationDate),
            ["applicationNo"] = F(Kind.Code, r => r.LoanApplication?.ApplicationNumber),
        },
        ["ucc1"] = new()
        {
            ["fileNumber"] = F(Kind.Code, r => r.Ucc1?.FileNumber),
            ["filingDate"] = F(Kind.Date, r => r.Ucc1?.FilingDate),
            ["filingOffice"] = F(Kind.Text, r => r.Ucc1?.FilingOffice),
            ["collateral"] = F(Kind.Text, r => r.Ucc1?.Collateral),
            ["debtorEntityType"] = F(Kind.Text, r => r.Ucc1?.DebtorEntityType),
            ["debtorJurisdiction"] = F(Kind.Text, r => r.Ucc1?.DebtorJurisdiction),
            ["debtorOrganizationId"] = F(Kind.Code, r => r.Ucc1?.DebtorOrganizationId),
            ["additionalDebtorName"] = F(Kind.Name, r => r.Ucc1?.AdditionalDebtorName),
            ["alternativeDesignation"] = F(Kind.Keyword, r => r.Ucc1?.AlternativeDesignation),
            ["realEstateRecords"] = Box(r => r.Ucc1?.RealEstateRecords),
            ["searchAllDebtors"] = Box(r => r.Ucc1?.SearchReport?.AllDebtors),
            ["searchDebtor1"] = Box(r => r.Ucc1?.SearchReport?.Debtor1),
            ["searchDebtor2"] = Box(r => r.Ucc1?.SearchReport?.Debtor2),
            ["filerReference"] = F(Kind.Text, r => r.Ucc1?.FilerReference),
            ["contactName"] = F(Kind.Text, r => r.Ucc1?.ContactName),
            ["acknowledgmentTo"] = F(Kind.Text, r => r.Ucc1?.AcknowledgmentTo),
        },
        ["ucc3"] = new()
        {
            ["initialFileNumber"] = F(Kind.Code, r => r.Ucc3?.InitialFileNumber),
            ["fileNumber"] = F(Kind.Code, r => r.Ucc3?.FileNumber),
            ["filingDate"] = F(Kind.Date, r => r.Ucc3?.FilingDate),
            ["action"] = F(Kind.Keyword, r => r.Ucc3?.Action),
            ["realEstateRecords"] = Box(r => r.Ucc3?.RealEstateRecords),
            ["termination"] = Box(r => r.Ucc3?.Termination),
            ["assignment"] = Box(r => r.Ucc3?.Assignment),
            ["continuation"] = Box(r => r.Ucc3?.Continuation),
            ["partyInformationChange"] = Box(r => r.Ucc3?.PartyInformationChange),
            ["partyChangeAffects"] = F(Kind.Keyword, r => r.Ucc3?.PartyChangeAffects),
            ["partyChangeType"] = F(Kind.Keyword, r => r.Ucc3?.PartyChangeType),
            ["assigneeName"] = F(Kind.Name, r => r.Ucc3?.AssigneeName),
            ["recordPartyName"] = F(Kind.Name, r => r.Ucc3?.RecordPartyName),
            ["changedPartyName"] = F(Kind.Name, r => r.Ucc3?.ChangedPartyName),
            ["collateralChange"] = F(Kind.Keyword, r => r.Ucc3?.CollateralChange),
            ["collateralChangeDescription"] = F(Kind.Text, r => r.Ucc3?.CollateralChangeDescription),
            ["authorizingPartyName"] = F(Kind.Name, r => r.Ucc3?.AuthorizingPartyName),
            ["authorizedByDebtor"] = Box(r => r.Ucc3?.AuthorizedByDebtor),
            ["filerReference"] = F(Kind.Text, r => r.Ucc3?.FilerReference),
            ["contactName"] = F(Kind.Text, r => r.Ucc3?.ContactName),
            ["contactEmail"] = F(Kind.Code, r => r.Ucc3?.ContactEmail),
            ["acknowledgmentTo"] = F(Kind.Text, r => r.Ucc3?.AcknowledgmentTo),
        },
        ["loan-security-agreement"] = new()
        {
            ["agreementDate"] = F(Kind.Date, r => r.SecurityAgreement?.AgreementDate),
            ["principal"] = F(Kind.Amount, r => r.SecurityAgreement?.Principal),
            ["loanType"] = F(Kind.Text, r => r.SecurityAgreement?.LoanType),
            ["governingLaw"] = F(Kind.Text, r => r.SecurityAgreement?.GoverningLaw),
        },
    };

    /// <summary>One check per fact. An empty expected value means the box is printed empty, so the model must leave it null.</summary>
    public static IReadOnlyList<Check> Score(string file, string rendering, string documentType, IReadOnlyDictionary<string, string> facts, ExtractionResult result)
    {
        var specs = ByType[documentType];
        var checks = new List<Check>();
        foreach (var (key, expected) in facts)
        {
            // On an assignment, item 7 holds the assignee, which a reader may reasonably report as the changed party too.
            if (key == "changedPartyName" && facts.GetValueOrDefault("action") == "Assignment")
                continue;
            if (!Common.TryGetValue(key, out var spec) && !specs.TryGetValue(key, out spec))
                throw new InvalidOperationException($"the manifest fact '{key}' ({documentType}) has no scorer in ExtractionScoring");

            var field = spec.Get(result);
            checks.Add(spec.Kind == Kind.Flag ? ScoreFlag(file, rendering, key, expected, field as ExtractedFlag)
                                              : ScoreField(file, rendering, key, expected, spec.Kind, field as ExtractedField));
        }
        return checks;
    }

    private static Check ScoreFlag(string file, string rendering, string key, string expected, ExtractedFlag? flag) =>
        new(file, rendering, key, flag?.Checked is { } c && c == bool.Parse(expected), expected, flag?.Checked?.ToString().ToLowerInvariant());

    private static Check ScoreField(string file, string rendering, string key, string expected, Kind kind, ExtractedField? field)
    {
        var actual = field?.Normalized ?? field?.Value;
        if (expected.Length == 0)
            return new Check(file, rendering, key, IsBlank(field?.Value) && IsBlank(field?.Normalized), "(empty)", actual);
        var pass = Matches(kind, field?.Normalized, expected) || Matches(kind, field?.Value, expected);
        return new Check(file, rendering, key, pass, expected, actual);
    }

    private static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s) || Alnum(s) is "NONE" or "NA";

    private static bool Matches(Kind kind, string? actual, string expected) => kind switch
    {
        Kind.Name => SameName(actual, expected),
        Kind.Code => Alnum(actual) == Alnum(expected) && Alnum(actual).Length > 0,
        Kind.Date => SameDate(actual, expected),
        Kind.Amount => SameAmount(actual, expected),
        Kind.Digits => Digits(actual) == Digits(expected) && Digits(actual).Length > 0,
        Kind.Text => Overlaps(actual, expected),
        Kind.Keyword => Alnum(actual).Contains(Alnum(expected), StringComparison.Ordinal) && Alnum(actual).Length > 0,
        _ => false
    };

    // ---- comparers -----------------------------------------------------------------------------------------------

    private static readonly Regex NonAlnum = NonAlnumRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnumRegex();

    private static readonly HashSet<string> NameNoise = ["llc", "inc", "na", "n", "a", "co", "corp", "corporation", "the", "of", "and", "company", "lp", "l", "p"];

    private static List<string> Tokens(string? s) => NonAlnum.Split((s ?? "").ToLowerInvariant()).Where(t => t.Length > 0).ToList();

    internal static bool SameName(string? actual, string expected)
    {
        var a = Tokens(actual).Where(t => !NameNoise.Contains(t)).ToList();
        var e = Tokens(expected).Where(t => !NameNoise.Contains(t)).ToList();
        return a.Count > 0 && (a.SequenceEqual(e) || a.All(e.Contains) && e.All(a.Contains) || string.Join("", a).Contains(string.Join("", e)));
    }

    internal static bool Overlaps(string? actual, string expected)
    {
        var a = Tokens(actual).Where(t => t.Length > 2).ToHashSet();
        var e = Tokens(expected).Where(t => t.Length > 2).ToHashSet();
        return a.Count > 0 && e.Count > 0 && a.Intersect(e).Count() >= Math.Ceiling(Math.Min(a.Count, e.Count) * 0.6);
    }

    internal static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    internal static string Alnum(string? s) => new((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    internal static bool SameAmount(string? actual, string expected) =>
        TryAmount(actual, out var a) && TryAmount(expected, out var e) && a == e;

    private static bool TryAmount(string? s, out decimal value) =>
        decimal.TryParse(new string((s ?? "").Where(c => char.IsDigit(c) || c == '.').ToArray()), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static readonly string[] DateFormats = ["MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "MMMM d, yyyy", "MMM d, yyyy", "MM/dd/yy"];

    internal static bool SameDate(string? actual, string expected) =>
        DateOnly.TryParseExact(actual?.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) &&
        DateOnly.TryParseExact(expected, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) && a == e;
}

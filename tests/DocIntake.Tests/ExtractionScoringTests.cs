using System.Text.Json;
using DocIntake.Api.Models;

namespace DocIntake.Tests;

public class ExtractionScoringTests
{
    private sealed record Entry(string File, string Rendering, string DocumentType, Dictionary<string, string> Facts);

    private static List<Entry> Manifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(LendingTypeDetectorTests.CorpusDirectory(), "manifest.json")));
        return doc.RootElement.EnumerateArray().Select(e => new Entry(
            e.GetProperty("File").GetString()!, e.GetProperty("Rendering").GetString()!, e.GetProperty("DocumentType").GetString()!,
            e.GetProperty("Facts").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!))).ToList();
    }

    private static ExtractionResult Nothing() => new(LendingDocumentType.Other, null!, null!, null, null, null, null, []);

    private static ExtractedField? V(Dictionary<string, string> facts, string key) =>
        facts.TryGetValue(key, out var v) && v.Length > 0 ? Extracted.Field(v) : Extracted.Field(null);

    private static ExtractedFlag B(Dictionary<string, string> facts, string key) => Extracted.Flag(bool.Parse(facts[key]));

    /// <summary>What a perfect model would return for the facts: the inverse of the scorers.</summary>
    private static ExtractionResult Perfect(Entry e)
    {
        var f = e.Facts;
        var borrower = new Borrower(V(f, "debtorName")!, Extracted.Field(null), V(f, "ein")!);
        var lender = new Party(V(f, "lender")!, Extracted.Field(null));
        return e.DocumentType switch
        {
            "loan-application" => new(LendingDocumentType.LoanApplication, borrower, lender,
                new LoanApplicationSection(V(f, "applicationNo")!, V(f, "applicationDate")!, V(f, "amountRequested")!, V(f, "termMonths")!, Extracted.Field(null)),
                null, null, null, []),
            "ucc1" => new(LendingDocumentType.Ucc1, borrower, lender, null,
                new Ucc1Section(V(f, "fileNumber")!, V(f, "filingDate")!, V(f, "filingOffice")!, Extracted.Field(null), V(f, "collateral")!,
                    V(f, "debtorEntityType")!, V(f, "debtorJurisdiction")!, V(f, "debtorOrganizationId")!, V(f, "additionalDebtorName")!, V(f, "alternativeDesignation")!,
                    B(f, "realEstateRecords"), new SearchReportRequest(B(f, "searchAllDebtors"), B(f, "searchDebtor1"), B(f, "searchDebtor2")),
                    V(f, "filerReference")!, V(f, "contactName")!, Extracted.Field(null), V(f, "acknowledgmentTo")!),
                null, null, []),
            "ucc3" => new(LendingDocumentType.Ucc3, borrower, lender, null, null,
                new Ucc3Section(V(f, "initialFileNumber")!, V(f, "fileNumber")!, V(f, "filingDate")!, Extracted.Field(null), V(f, "action")!,
                    B(f, "realEstateRecords"), B(f, "termination"), B(f, "assignment"), V(f, "assigneeName")!, B(f, "continuation"), B(f, "partyInformationChange"),
                    V(f, "partyChangeAffects")!, V(f, "partyChangeType")!, V(f, "recordPartyName")!, V(f, "changedPartyName")!, V(f, "collateralChange")!,
                    V(f, "collateralChangeDescription")!, V(f, "authorizingPartyName")!, B(f, "authorizedByDebtor"), V(f, "filerReference")!,
                    V(f, "contactName")!, V(f, "contactEmail")!, V(f, "acknowledgmentTo")!),
                null, []),
            _ => new(LendingDocumentType.LoanSecurityAgreement, borrower, lender, null, null, null,
                new SecurityAgreementSection(V(f, "agreementDate")!, V(f, "principal")!, V(f, "loanType")!, Extracted.Field(null), V(f, "governingLaw")!, Extracted.Field(null)), [])
        };
    }

    [Fact]
    public void Every_manifest_fact_has_a_scorer()
    {
        foreach (var e in Manifest())
        {
            var checks = ExtractionScoring.Score(e.File, e.Rendering, e.DocumentType, e.Facts, Nothing());

            var skipped = e.Facts.ContainsKey("changedPartyName") && e.Facts["action"] == "Assignment" ? 1 : 0;
            Assert.Equal(e.Facts.Count - skipped, checks.Count);
        }
    }

    [Fact]
    public void A_perfect_extraction_scores_every_check()
    {
        foreach (var e in Manifest())
        {
            var failed = ExtractionScoring.Score(e.File, e.Rendering, e.DocumentType, e.Facts, Perfect(e)).Where(c => !c.Pass).ToList();

            Assert.True(failed.Count == 0, $"{e.File}: " + string.Join(", ", failed.Select(c => $"{c.Field} (expected '{c.Expected}', got '{c.Actual}')")));
        }
    }

    [Fact]
    public void An_empty_extraction_fails_the_filled_facts_but_passes_the_ones_printed_empty()
    {
        var e = Manifest().First(m => m.DocumentType == "ucc1");

        var checks = ExtractionScoring.Score(e.File, e.Rendering, e.DocumentType, e.Facts, Nothing()).ToDictionary(c => c.Field);

        Assert.True(checks["additionalDebtorName"].Pass);       // empty box, nothing invented
        Assert.False(checks["fileNumber"].Pass);
        Assert.False(checks["realEstateRecords"].Pass);          // "unknown" is not "unchecked"
    }

    [Fact]
    public void Inventing_a_value_for_an_empty_box_or_misreading_a_checkbox_fails()
    {
        var e = Manifest().First(m => m.DocumentType == "ucc1");
        var perfect = Perfect(e);
        perfect.Ucc1!.AdditionalDebtorName.Value = "Acme Holdings LLC";
        perfect.Ucc1.SearchReport.AllDebtors.Checked = true;

        var failed = ExtractionScoring.Score(e.File, e.Rendering, e.DocumentType, e.Facts, perfect).Where(c => !c.Pass).Select(c => c.Field);

        Assert.Equal(["additionalDebtorName", "searchAllDebtors"], failed.Order());
    }

    [Fact]
    public void On_an_assignment_the_changed_party_name_is_not_scored()
    {
        var e = Manifest().First(m => m.DocumentType == "ucc3" && m.Facts["action"] == "Assignment");
        var result = Perfect(e);
        result.Ucc3!.ChangedPartyName.Value = e.Facts["assigneeName"];

        var checks = ExtractionScoring.Score(e.File, e.Rendering, e.DocumentType, e.Facts, result);

        Assert.DoesNotContain(checks, c => c.Field == "changedPartyName");
        Assert.All(checks, c => Assert.True(c.Pass, c.Field));
    }

    [Theory]
    [InlineData("Redwood Mechanical Supply LLC", "Redwood Mechanical Supply, LLC", true)]
    [InlineData("CASCADE HARBOR BANK N.A.", "Cascade Harbor Bank, N.A.", true)]
    [InlineData("Cascade Harbor", "Cascade Harbor Bank, N.A.", false)]
    [InlineData(null, "Cascade Harbor Bank, N.A.", false)]
    public void Names_ignore_case_punctuation_and_company_suffixes(string? actual, string expected, bool same) =>
        Assert.Equal(same, ExtractionScoring.SameName(actual, expected));

    [Theory]
    [InlineData("2026-02-12", "02/12/2026", true)]
    [InlineData("February 12, 2026", "02/12/2026", true)]
    [InlineData("02/13/2026", "02/12/2026", false)]
    [InlineData("not a date", "02/12/2026", false)]
    public void Dates_match_across_formats(string actual, string expected, bool same) =>
        Assert.Equal(same, ExtractionScoring.SameDate(actual, expected));

    [Theory]
    [InlineData("750000.00", "$750,000", true)]
    [InlineData("$750,000.00", "$750,000.00", true)]
    [InlineData("75000", "$750,000", false)]
    public void Amounts_ignore_symbols_and_separators(string actual, string expected, bool same) =>
        Assert.Equal(same, ExtractionScoring.SameAmount(actual, expected));

    [Fact]
    public void Free_text_needs_most_of_the_shorter_side()
    {
        Assert.True(ExtractionScoring.Overlaps("Equipment, inventory and accounts", "Equipment, Inventory, Accounts"));
        Assert.False(ExtractionScoring.Overlaps("Real estate", "Equipment, Inventory, Accounts"));
    }
}

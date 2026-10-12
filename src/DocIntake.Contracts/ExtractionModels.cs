using System.ComponentModel;

namespace DocIntake.Contracts;

public enum LendingDocumentType { LoanApplication, Ucc1, Ucc3, LoanSecurityAgreement, Other }

public enum Confidence { High, Medium, Low }

[Description("One extracted value. Value is the text as printed; Normalized is the machine-readable form " +
             "(ISO yyyy-MM-dd dates, plain decimal amounts without symbols or commas, 9-digit EINs). " +
             "Everything is null when the document does not state the value.")]
public sealed class ExtractedField
{
    [Description("The value exactly as printed in the document, or null if absent.")]
    public string? Value { get; set; }

    [Description("Normalized value, or null if absent.")]
    public string? Normalized { get; set; }

    [Description("1-based page the value was read from, or null if absent.")]
    public int? Page { get; set; }

    [Description("A short verbatim quote (at most 200 characters) that shows the value, or null if absent.")]
    public string? Evidence { get; set; }

    public Confidence Confidence { get; set; }
}

[Description("A checkbox on the form. Checked is true when the box is marked, false when it is clearly empty, null when it " +
             "cannot be told (not on this form, or illegible).")]
public sealed class ExtractedFlag
{
    public bool? Checked { get; set; }

    [Description("1-based page the box is on, or null if absent.")]
    public int? Page { get; set; }

    [Description("The box's printed label, e.g. '1b. This FINANCING STATEMENT AMENDMENT is to be filed [for record]', or null if absent.")]
    public string? Evidence { get; set; }

    public Confidence Confidence { get; set; }
}

[Description("Any other labelled value printed on the document that none of the named fields captures.")]
public sealed record NameValue(
    [property: Description("The label as printed (e.g. 'County', 'Notary commission expires').")] string Name,
    [property: Description("The value as printed.")] string Value,
    [property: Description("1-based page it appears on.")] int? Page);

public sealed record Party(ExtractedField Name, ExtractedField Address);

public sealed record Borrower(ExtractedField Name, ExtractedField Address, ExtractedField Ein);

public sealed record LoanApplicationSection(
    ExtractedField ApplicationNumber, ExtractedField ApplicationDate, ExtractedField AmountRequested,
    ExtractedField TermMonths, ExtractedField Purpose);

public sealed record SearchReportRequest(
    [property: Description("Box 7: All Debtors.")] ExtractedFlag AllDebtors,
    [property: Description("Box 7: Debtor 1.")] ExtractedFlag Debtor1,
    [property: Description("Box 7: Debtor 2.")] ExtractedFlag Debtor2);

public sealed record Ucc1Section(
    [property: Description("Assigned by the filing office; usually stamped, not in a numbered box.")] ExtractedField FileNumber,
    ExtractedField FilingDate,
    [property: Description("The filing office named on the form, e.g. a Secretary of State.")] ExtractedField FilingOffice,
    [property: Description("The state (jurisdiction) the statement is filed in.")] ExtractedField Jurisdiction,
    [property: Description("Box 4, summarised in one line.")] ExtractedField Collateral,
    [property: Description("Debtor's type of organization (e.g. LLC, corporation), from the debtor block.")] ExtractedField DebtorEntityType,
    [property: Description("Debtor's jurisdiction of organization (state), from the debtor block.")] ExtractedField DebtorJurisdiction,
    [property: Description("Debtor's organizational ID number, from the debtor block.")] ExtractedField DebtorOrganizationId,
    [property: Description("Box 2: the additional debtor's exact full legal name; null when box 2 is empty.")] ExtractedField AdditionalDebtorName,
    [property: Description("Box 5: which alternative designation is checked: LesseeLessor, ConsigneeConsignor, BaileeBailor, SellerBuyer, AgLien or NonUcc; None when no box is checked.")] ExtractedField AlternativeDesignation,
    [property: Description("Box 6: to be filed in the REAL ESTATE RECORDS.")] ExtractedFlag RealEstateRecords,
    SearchReportRequest SearchReport,
    [property: Description("Box 8: optional filer reference data.")] ExtractedField FilerReference,
    [property: Description("Header box A: name and phone of the contact at the filer, as printed.")] ExtractedField ContactName,
    [property: Description("The contact's e-mail, only if this form version has a box for it; null otherwise.")] ExtractedField ContactEmail,
    [property: Description("Header: who to send the acknowledgment to, name and address as printed.")] ExtractedField AcknowledgmentTo);

public sealed record Ucc3Section(
    [property: Description("The file number of the initial financing statement this amends (box 1a).")] ExtractedField InitialFileNumber,
    ExtractedField FileNumber, ExtractedField FilingDate, ExtractedField FilingOffice,
    [property: Description("Termination, Continuation, Assignment, or Amendment, from the box that is checked.")] ExtractedField Action,
    [property: Description("Box 1b: to be filed in the REAL ESTATE RECORDS.")] ExtractedFlag RealEstateRecords,
    [property: Description("Box 2: TERMINATION is checked.")] ExtractedFlag Termination,
    [property: Description("Box 3: ASSIGNMENT is checked.")] ExtractedFlag Assignment,
    [property: Description("Box 3: the assignee's name (organization, or surname and first name together); null if not an assignment.")] ExtractedField AssigneeName,
    [property: Description("Box 4: CONTINUATION is checked.")] ExtractedFlag Continuation,
    [property: Description("Box 5: PARTY INFORMATION CHANGE is checked, i.e. one of its three action boxes is marked.")] ExtractedFlag PartyInformationChange,
    [property: Description("Box 5: whom the change affects: Debtor, SecuredParty or None.")] ExtractedField PartyChangeAffects,
    [property: Description("Box 5: what the change does: Change, Add, Delete or None.")] ExtractedField PartyChangeType,
    [property: Description("Box 6: the current record information, i.e. the name of the party whose record is changed.")] ExtractedField RecordPartyName,
    [property: Description("Box 7: the changed or added party name of a party information change; null if none.")] ExtractedField ChangedPartyName,
    [property: Description("Box 8: collateral change: Add, Delete, Restate, Assign or None.")] ExtractedField CollateralChange,
    [property: Description("Box 8: the collateral description that is added, deleted, restated or assigned; null if none.")] ExtractedField CollateralChangeDescription,
    [property: Description("Box 9: name of the secured party of record authorizing this amendment (the debtor's name if the amendment is authorized by a debtor).")] ExtractedField AuthorizingPartyName,
    [property: Description("Box 9: 'Amendment authorized by a DEBTOR' is checked.")] ExtractedFlag AuthorizedByDebtor,
    [property: Description("Last box: optional filer reference data (box 10).")] ExtractedField FilerReference,
    [property: Description("Header box A: name and phone of the contact at the submitter, as printed.")] ExtractedField ContactName,
    [property: Description("Header box B: e-mail of the contact at the submitter.")] ExtractedField ContactEmail,
    [property: Description("Header box C: who to send the acknowledgment to, name and address as printed.")] ExtractedField AcknowledgmentTo);

public sealed record SecurityAgreementSection(
    ExtractedField AgreementDate, ExtractedField Principal, ExtractedField LoanType, ExtractedField Term,
    ExtractedField GoverningLaw, ExtractedField Collateral);

// One strict JSON-schema root per agent. Borrower is the debtor and Lender the secured party on UCC filings.
public sealed record LoanApplicationExtraction(Borrower Borrower, Party Lender, LoanApplicationSection LoanApplication, IReadOnlyList<NameValue> OtherFields);

public sealed record Ucc1Extraction(Borrower Borrower, Party Lender, Ucc1Section Ucc1, IReadOnlyList<NameValue> OtherFields);

public sealed record Ucc3Extraction(Borrower Borrower, Party Lender, Ucc3Section Ucc3, IReadOnlyList<NameValue> OtherFields);

public sealed record SecurityAgreementExtraction(Borrower Borrower, Party Lender, SecurityAgreementSection SecurityAgreement, IReadOnlyList<NameValue> OtherFields);

public sealed record GenericExtraction(
    [property: Description("Which kind of lending document this is; Other if none of the four.")] LendingDocumentType DocumentType,
    Borrower Borrower, Party Lender,
    [property: Description("Only for a loan application, otherwise null.")] LoanApplicationSection? LoanApplication,
    [property: Description("Only for a UCC-1 financing statement, otherwise null.")] Ucc1Section? Ucc1,
    [property: Description("Only for a UCC-3 amendment, otherwise null.")] Ucc3Section? Ucc3,
    [property: Description("Only for a loan / security agreement, otherwise null.")] SecurityAgreementSection? SecurityAgreement,
    IReadOnlyList<NameValue> OtherFields);

/// <summary>What every agent produces, whichever schema it used.</summary>
public sealed record ExtractionResult(
    LendingDocumentType DocumentType, Borrower Borrower, Party Lender,
    LoanApplicationSection? LoanApplication, Ucc1Section? Ucc1, Ucc3Section? Ucc3, SecurityAgreementSection? SecurityAgreement,
    IReadOnlyList<NameValue> OtherFields);

/// <summary>
/// Model tokens for one extraction (all of its model calls). <see cref="InputTokens"/> includes the cached ones and
/// <see cref="OutputTokens"/> includes the reasoning ones, as the provider counts them. <see cref="CostUsd"/> is the
/// list price (null when a model has no configured price).
/// </summary>
public sealed record ExtractionUsage(long InputTokens, long OutputTokens, long CachedInputTokens = 0, long? ReasoningTokens = null, decimal? CostUsd = null)
{
    /// <summary>The sum of two calls. A cost is only known when both are.</summary>
    public ExtractionUsage Plus(ExtractionUsage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CachedInputTokens + other.CachedInputTokens,
        ReasoningTokens is null && other.ReasoningTokens is null ? null : (ReasoningTokens ?? 0) + (other.ReasoningTokens ?? 0),
        CostUsd is { } a && other.CostUsd is { } b ? a + b : null);
}

public sealed record ExtractDocumentResponse(
    string FileName,
    LendingDocumentType DocumentType,
    [property: Description("keywords when the document type was recognised from its text, model when the generic agent decided.")] string DetectedBy,
    string Agent,
    string Model,
    ExtractionResult Extraction,
    ExtractionUsage Usage,
    IReadOnlyList<string> Warnings,
    [property: Description("Findings about the upload and the answer: active content, macros, executable attachments, hidden text, instruction-like text. Blocking ones refuse the request unless Security:OnFinding is flag.")]
    IReadOnlyList<RiskFinding>? Risks = null);

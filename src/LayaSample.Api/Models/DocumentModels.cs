namespace LayaSample.Api.Models;

public enum DocumentKind
{
    FormPdf,
    /// <summary>Dynamic XFA form: pages are a viewer placeholder; the data lives in the XFA datasets XML.</summary>
    XfaPdf,
    AnnotatedPdf,
    TextPdf,
    ScannedPdf,
    FaxPdf,
    /// <summary>Has a text layer, but it decodes to garbage (missing ToUnicode / broken font encoding).</summary>
    BrokenTextPdf,
    /// <summary>Table- or drawing-heavy pages where plain text loses the layout.</summary>
    LayoutPdf,
    MixedPdf,
    /// <summary>TIFF (including multi-page fax), PNG or JPEG.</summary>
    Image,
    Spreadsheet,
    WordDocument
}

public static class DocumentKindExtensions
{
    public static bool IsPdf(this DocumentKind kind) =>
        kind is not (DocumentKind.Spreadsheet or DocumentKind.WordDocument or DocumentKind.Image);

    public static bool IsPaged(this DocumentKind kind) => kind.IsPdf() || kind == DocumentKind.Image;
}

public enum PreparationStrategy
{
    RenderToPng,
    Markdown,
    /// <summary>Page image plus its text (text layer, or OCR when the text layer is missing or unusable).</summary>
    Hybrid,
    /// <summary>Only machine-readable data: form values, XFA datasets, embedded XML.</summary>
    StructuredData,
    AsIs,
    /// <summary>Document-level marker: each page has its own strategy (see <see cref="PageClassification.Strategy"/>).</summary>
    PerPage
}

public enum PageContent { Text, Scanned, Fax, BrokenText, OcrLayer, Layout, Form, Annotated, XfaPlaceholder }

/// <param name="RenderDpi">Render resolution when it differs from the default (fax pages render at native resolution).</param>
public sealed record PageClassification(
    int Number,
    PageContent Content,
    PreparationStrategy Strategy,
    int Chars,
    int Images,
    double GarbageRatio,
    int? RenderDpi);

/// <summary>
/// A digital signature. <see cref="IntegrityValid"/> means the signed bytes are unmodified and match the signer's
/// certificate; it does not mean the certificate chains to a trusted root.
/// </summary>
public sealed record SignatureInfo(
    string? FieldName,
    string? Signer,
    DateTimeOffset? SigningTime,
    bool CoversWholeDocument,
    bool? IntegrityValid,
    string? Error);

public sealed record AttachmentInfo(string Name, long Size, string MediaType);

public sealed record DocumentClassification
{
    public required DocumentKind Kind { get; init; }
    public required PreparationStrategy Strategy { get; init; }
    public required string Route { get; init; }
    public required string Reason { get; init; }
    public required string MediaType { get; init; }

    public int PageCount { get; init; }
    public int FormFieldCount { get; init; }
    public int FilledFormFieldCount { get; init; }
    public int MarkupAnnotationCount { get; init; }
    public int ImageCount { get; init; }
    public double AvgCharsPerPage { get; init; }
    public bool IsXfa { get; init; }
    public bool IsDynamicXfa { get; init; }

    public IReadOnlyList<PageClassification> Pages { get; init; } = [];
    public IReadOnlyList<SignatureInfo> Signatures { get; init; } = [];
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
}

public enum PartRole { PageImage, PageText, OcrText, StructuredData, Original }

/// <summary>One piece of prepared content. <see cref="Source"/> names the attachment it came from (null = the uploaded document).</summary>
public sealed record PreparedPart(PartRole Role, string MediaType, int? Page, string? Text, byte[]? Data, string? Source = null);

public sealed record PreparedDocument(PreparationStrategy Strategy, IReadOnlyList<PreparedPart> Parts);

public sealed record PreparedPartDto(PartRole Role, string MediaType, int? Page, string? Source, string? Text, string? DataBase64);

public sealed record AgentResult(string Agent, string Status, string Message);

public sealed record AnalyzeDocumentResponse(
    string FileName,
    DocumentClassification Classification,
    PreparationStrategy AppliedStrategy,
    IReadOnlyList<PreparedPartDto> Parts,
    AgentResult? Agent);

namespace LayaSample.Api.Models;

public enum DocumentKind { FormPdf, TextPdf, ScannedPdf, MixedPdf, Spreadsheet, WordDocument }

public enum PreparationStrategy { RenderToPng, Markdown, AsIs }

public sealed record DocumentClassification(
    DocumentKind Kind,
    PreparationStrategy Strategy,
    string Route,
    string Reason,
    int PageCount,
    int FormFieldCount,
    int FilledFormFieldCount,
    int ImageCount,
    double AvgCharsPerPage);

/// <summary>One piece of prepared content: a PNG page, a markdown text, or the original bytes.</summary>
public sealed record PreparedPart(string MediaType, int? Page, string? Text, byte[]? Data);

public sealed record PreparedDocument(PreparationStrategy Strategy, IReadOnlyList<PreparedPart> Parts);

public sealed record PreparedPartDto(string MediaType, int? Page, string? Text, string? DataBase64);

public sealed record AgentResult(string Agent, string Status, string Message);

public sealed record AnalyzeDocumentResponse(
    string FileName,
    DocumentClassification Classification,
    PreparationStrategy AppliedStrategy,
    IReadOnlyList<PreparedPartDto> Parts,
    AgentResult? Agent);

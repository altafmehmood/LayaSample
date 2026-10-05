namespace LayaSample.Api.Services.Documents;

public sealed class DocumentAnalysisOptions
{
    public const string Section = "DocumentAnalysis";

    public long MaxFileBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>A PDF page with fewer extractable characters than this is treated as image-only.</summary>
    public int MinCharsPerPage { get; set; } = 50;

    public int PngDpi { get; set; } = 150;

    /// <summary>Upper bound on pages rendered to PNG, to keep responses a sane size.</summary>
    public int MaxPages { get; set; } = 20;
}

/// <summary>A problem with the uploaded document that maps to a specific HTTP status.</summary>
public sealed class DocumentException(string message, int statusCode, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}

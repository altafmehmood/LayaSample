using LayaSample.Api.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace LayaSample.Api.Services.Documents.Pdf;

/// <summary>Raw per-page measurements gathered by <see cref="IPdfDetector"/>s; turned into decisions by <see cref="PdfClassificationRules"/>.</summary>
public sealed class PageSignals(int number)
{
    public int Number { get; } = number;
    public int Chars { get; set; }
    public int BadChars { get; set; }
    public int NonSpaceChars { get; set; }
    public int AlphanumericChars { get; set; }
    public int Images { get; set; }
    public bool HasFaxImage { get; set; }
    public bool HasFullPageImage { get; set; }
    public int? NativeImageDpi { get; set; }
    public int Paths { get; set; }
    public int Widgets { get; set; }
    public int MarkupAnnotations { get; set; }

    public double GarbageRatio => Chars == 0 ? 0 : (double)BadChars / Chars;
    public double AlphanumericRatio => NonSpaceChars == 0 ? 1 : (double)AlphanumericChars / NonSpaceChars;
}

public sealed class DocumentSignals
{
    public int FormFields { get; set; }
    public int FilledFormFields { get; set; }
    public bool IsXfa { get; set; }
    public bool IsDynamicXfa { get; set; }
    public bool HasXfaData { get; set; }
    public List<SignatureInfo> Signatures { get; } = [];
    public List<AttachmentInfo> Attachments { get; } = [];
    public List<RiskFinding> Risks { get; } = [];

    /// <summary>Records a finding once: a form with a hundred JavaScript fields is one finding, not a hundred.</summary>
    public void AddRisk(string kind, RiskSeverity severity, int? page, string detail)
    {
        if (!Risks.Any(r => r.Kind == kind && r.Severity == severity && r.Detail == detail))
            Risks.Add(new RiskFinding(kind, severity, page, detail));
    }
}

public sealed class PdfInspection(PdfDocument pdf, Stream file, DocumentAnalysisOptions options)
{
    public PdfDocument Pdf { get; } = pdf;

    /// <summary>
    /// The original file, seekable (needed for signature byte ranges). PdfPig reads it lazily, so a reader must put the
    /// position back.
    /// </summary>
    public Stream File { get; } = file;

    public DocumentAnalysisOptions Options { get; } = options;
    public DocumentSignals Document { get; } = new();
    public List<PageSignals> Pages { get; } = [];
}

/// <summary>
/// One independent check over a PDF. Document-level checks run once; page-level checks run during a single
/// shared pass over the pages so each page is parsed only once. Add a detector to recognise a new kind of PDF.
/// </summary>
public interface IPdfDetector
{
    void InspectDocument(PdfInspection inspection) { }
    void InspectPage(PdfInspection inspection, Page page, PageSignals signals) { }
}

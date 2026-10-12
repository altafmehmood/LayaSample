using DocIntake.Contracts;
using DocIntake.Api.Services.Security;
using DocIntake.Api.Services.Usage;
using UglyToad.PdfPig.AcroForms;
using UglyToad.PdfPig.AcroForms.Fields;
using UglyToad.PdfPig.Tokens;

namespace DocIntake.Api.Services.Documents.Pdf;

/// <summary>Counts AcroForm fields and verifies digital signatures.</summary>
public sealed class AcroFormDetector : IPdfDetector
{
    public void InspectDocument(PdfInspection inspection)
    {
        if (!inspection.Pdf.TryGetForm(out var form) || form is null) return;

        foreach (var (_, field) in PdfStructuredData.Fields(form))
        {
            switch (field)
            {
                case AcroSignatureField signature:
                    if (PdfSignatureVerifier.Verify(inspection.Pdf, signature, inspection.File) is { } info)
                        inspection.Document.Signatures.Add(info);
                    break;
                case { FieldType: AcroFieldType.PushButton }:
                    break;
                default:
                    inspection.Document.FormFields++;
                    if (PdfStructuredData.HasValue(PdfStructuredData.FieldValue(field))) inspection.Document.FilledFormFields++;
                    break;
            }
        }
    }
}

/// <summary>
/// Detects XFA forms. Dynamic XFA (NeedsRendering) pages are only a "please upgrade your viewer" placeholder;
/// the filled data is in the XFA datasets packet.
/// </summary>
public sealed class XfaDetector : IPdfDetector
{
    public void InspectDocument(PdfInspection inspection)
    {
        var acroForm = PdfTokens.AcroForm(inspection.Pdf);
        if (acroForm is null || !acroForm.Data.ContainsKey("XFA")) return;

        inspection.Document.IsXfa = true;
        inspection.Document.IsDynamicXfa =
            PdfTokens.Get<BooleanToken>(inspection.Pdf, PdfTokens.Catalog(inspection.Pdf), "NeedsRendering")?.Data == true;
        inspection.Document.HasXfaData = PdfStructuredData.ReadXfaDatasets(inspection.Pdf) is not null;
    }
}

/// <summary>Lists embedded files (e-invoice XML such as ZUGFeRD / Factur-X, portfolios).</summary>
public sealed class EmbeddedFileDetector : IPdfDetector
{
    public void InspectDocument(PdfInspection inspection)
    {
        foreach (var (name, bytes) in PdfStructuredData.ReadAttachments(inspection.Pdf))
        {
            inspection.Document.Attachments.Add(new AttachmentInfo(name, bytes.Length, DocumentSniffer.Sniff(bytes)));
            if (ExecutableSniffer.Describe(name, bytes.Span) is { } what)
                inspection.Document.AddRisk(RiskKinds.ExecutableAttachment, RiskSeverity.Block, null, $"embedded file '{UsageFileName.Clean(name)}' looks like {what}");
        }
    }
}

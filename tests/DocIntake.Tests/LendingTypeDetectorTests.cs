using System.Text.Json;
using DocIntake.Contracts;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using DocIntake.Rendering;

namespace DocIntake.Tests;

public class LendingTypeDetectorTests
{
    internal static string CorpusDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "samples", "pdf", "corpus");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("docs/samples/pdf/corpus not found above " + AppContext.BaseDirectory);
    }

    internal static PreparedDocument Prepare(byte[] bytes)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new DocumentAnalysisOptions());
        var rasterizer = new LocalPageRasterizer(new FakeOcr(), Microsoft.Extensions.Options.Options.Create(new RenderingOptions()));
        var classifier = new DocumentClassifier(opts, rasterizer);
        var preparer = new DocumentPreparer(opts, classifier, rasterizer, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentPreparer>.Instance);
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream(bytes);
        var classification = classifier.ClassifyAsync(stream, ct).GetAwaiter().GetResult();
        return preparer.PrepareAsync(stream, classification, ct).GetAwaiter().GetResult();
    }

    private static LendingDocumentType Expected(string documentType) => documentType switch
    {
        "loan-application" => LendingDocumentType.LoanApplication,
        "ucc1" => LendingDocumentType.Ucc1,
        "ucc3" => LendingDocumentType.Ucc3,
        "loan-security-agreement" => LendingDocumentType.LoanSecurityAgreement,
        _ => throw new ArgumentOutOfRangeException(nameof(documentType), documentType, null)
    };

    // Scans and faxes need real OCR to be recognised; the evaluation harness covers them. Everything with a text layer
    // or form fields is checked here.
    public static TheoryData<string, string> TextBearingCorpus()
    {
        var data = new TheoryData<string, string>();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(CorpusDirectory(), "manifest.json")));
        foreach (var e in manifest.RootElement.EnumerateArray())
            if (e.GetProperty("Rendering").GetString() is "digital" or "form" or "mixed")
                data.Add(e.GetProperty("File").GetString()!, e.GetProperty("DocumentType").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(TextBearingCorpus))]
    public void Corpus_documents_with_text_are_recognised(string file, string documentType)
    {
        var prepared = Prepare(File.ReadAllBytes(Path.Combine(CorpusDirectory(), file)));

        Assert.Equal(Expected(documentType), LendingTypeDetector.Detect(prepared));
    }

    [Fact]
    public void Document_without_recognisable_text_is_other()
    {
        Assert.Equal(LendingDocumentType.Other, LendingTypeDetector.Detect(Prepare(DocumentFixtures.TextPdf())));
        Assert.Equal(LendingDocumentType.Other, LendingTypeDetector.Detect(new PreparedDocument(PreparationStrategy.RenderToPng, [])));
    }

    [Theory]
    [InlineData("U C C  Financing-Statement\nAMENDMENT", LendingDocumentType.Ucc3)]
    [InlineData("ucc financing statement", LendingDocumentType.Ucc1)]
    [InlineData("LOAN AND\nSECURITY AGREEMENT\nThe Lender will file a UCC financing statement", LendingDocumentType.LoanSecurityAgreement)]
    [InlineData("Commercial Loan Application", LendingDocumentType.LoanApplication)]
    public void Matching_ignores_case_spacing_and_punctuation_as_ocr_disturbs_them(string firstPage, LendingDocumentType expected)
    {
        var document = new PreparedDocument(PreparationStrategy.Hybrid, [new PreparedPart(PartRole.OcrText, "text/plain", 1, firstPage, null)]);

        Assert.Equal(expected, LendingTypeDetector.Detect(document));
    }

    [Fact]
    public void Only_the_first_page_decides()
    {
        var document = new PreparedDocument(PreparationStrategy.PerPage,
        [
            new PreparedPart(PartRole.PageText, "text/markdown", 1, "Some cover letter", null),
            new PreparedPart(PartRole.PageText, "text/markdown", 2, "UCC FINANCING STATEMENT", null)
        ]);

        Assert.Equal(LendingDocumentType.Other, LendingTypeDetector.Detect(document));
    }
}

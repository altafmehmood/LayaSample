using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents;
using LayaSample.Api.Services.Security;
using LayaSample.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayaSample.Tests;

/// <summary>
/// The sample scans and faxes through the real OCR engine (local ONNX models, no network, no cost), checking that what OCR
/// reads from ordinary paperwork does not trip the injection patterns or the structure checks. Skipped unless
/// REAL_OCR=1, because OCR of the whole corpus takes minutes.
/// Run: <c>REAL_OCR=1 dotnet run --project tests/LayaSample.Tests -c Release -- -trait "Category=Integration"</c>
/// </summary>
public class RealOcrRiskTests
{
    private sealed class NoMeters : System.Diagnostics.Metrics.IMeterFactory
    {
        public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) => new(options);
        public void Dispose() { }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Scans_and_faxes_read_by_the_real_ocr_have_no_blocking_findings()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("REAL_OCR") == "1", "set REAL_OCR=1 to OCR the sample scans and faxes (minutes, no network)");
        var ct = TestContext.Current.CancellationToken;
        var options = Options.Create(new DocumentAnalysisOptions());
        using var ocr = new RapidOcrEngine(Options.Create(new RenderingOptions()));
        var rasterizer = new LocalPageRasterizer(ocr, Options.Create(new RenderingOptions()));
        var classifier = new DocumentClassifier(options, rasterizer);
        var preparer = new DocumentPreparer(options, classifier, rasterizer, NullLogger<DocumentPreparer>.Instance);
        var scanner = new RiskScanner(Options.Create(new SecurityOptions { OnFinding = "flag" }), new DocumentMetrics(new NoMeters()), NullLogger<RiskScanner>.Instance);

        var files = Directory.EnumerateFiles(LendingTypeDetectorTests.CorpusDirectory(), "*.pdf")
            .Where(f => f.EndsWith("-scan.pdf") || f.EndsWith("-fax.pdf")).ToList();
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        var characters = 0;
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            var classification = await classifier.ClassifyAsync(stream, ct);
            var prepared = await preparer.PrepareAsync(stream, classification, ct);
            characters += prepared.Parts.Where(p => p.Role == PartRole.OcrText).Sum(p => p.Text?.Length ?? 0);
            offenders.AddRange(scanner.CheckStructure(classification).Concat(scanner.CheckText(prepared))
                .Where(f => f.Severity == RiskSeverity.Block).Select(f => $"{Path.GetFileName(file)}: {f.Kind} {f.Detail}"));
        }

        Assert.True(characters > 1000, "OCR actually read text, so the check means something");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }
}

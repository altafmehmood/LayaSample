using System.Globalization;
using System.Text.Json;
using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents;
using LayaSample.Api.Services.Documents.Extraction;
using LayaSample.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LayaSample.Tests;

/// <summary>
/// Runs the real pipeline (real OCR, real model) over the lending corpus and scores it against the manifest's facts.
/// Skipped unless a key is set (OPENAI_API_KEY, or the Extraction:ApiKey user secret), so it never runs in CI's normal build. Calls the paid API: 39 documents.
/// Optional: EVAL_VISION_MODEL, EVAL_TEXT_MODEL, EVAL_FILTER (substring of the file name), EVAL_MIN_ACCURACY (default 0.9).
/// Run: dotnet run --project tests/LayaSample.Tests -c Release -- -trait "Category=Evaluation" -showLiveOutput
/// </summary>
public class ExtractionEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Evaluation")]
    public async Task Corpus_accuracy_meets_the_threshold()
    {
        // The environment variable wins; otherwise the same user secret the API reads in Development.
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).Build()["Extraction:ApiKey"];
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(apiKey), "set OPENAI_API_KEY or the Extraction:ApiKey user secret to run the extraction evaluation");

        var settings = new ExtractionOptions
        {
            VisionModel = Environment.GetEnvironmentVariable("EVAL_VISION_MODEL") ?? "gpt-5.4-mini",
            TextModel = Environment.GetEnvironmentVariable("EVAL_TEXT_MODEL") ?? "gpt-5.4-mini",
            ApiKey = apiKey
        };
        var filter = Environment.GetEnvironmentVariable("EVAL_FILTER");
        var threshold = double.Parse(Environment.GetEnvironmentVariable("EVAL_MIN_ACCURACY") ?? "0.9", CultureInfo.InvariantCulture);
        var ct = TestContext.Current.CancellationToken;

        var docOptions = Options.Create(new DocumentAnalysisOptions { AllowInProcessRendering = true });
        using var ocr = new RapidOcrEngine(Options.Create(new RenderingOptions()));
        var rasterizer = new LocalPageRasterizer(ocr, Options.Create(new RenderingOptions()));
        var classifier = new DocumentClassifier(docOptions, rasterizer);
        var preparer = new DocumentPreparer(docOptions, classifier, rasterizer, NullLogger<DocumentPreparer>.Instance);
        IExtractionAgent[] agents = [new LoanApplicationAgent(), new Ucc1Agent(), new Ucc3Agent(), new SecurityAgreementAgent(), new GenericLendingAgent()];
        var extractor = new DocumentExtractor(Options.Create(settings), ExtractionClientFactory.Create(settings), new OpenAiExtractionErrorMapper(), agents,
            new DocumentMetrics(new NoMeterFactory()), NullLogger<DocumentExtractor>.Instance);

        var corpus = LendingTypeDetectorTests.CorpusDirectory();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(corpus, "manifest.json")));
        var entries = manifest.RootElement.EnumerateArray().Where(e => filter is null || e.GetProperty("File").GetString()!.Contains(filter)).ToList();

        var checks = new List<Check>();
        var detection = new List<(string File, string Rendering, bool Correct)>();
        var failures = new List<string>();
        var gate = new SemaphoreSlim(4);
        var results = await Task.WhenAll(entries.Select(async e =>
        {
            var file = e.GetProperty("File").GetString()!;
            var rendering = e.GetProperty("Rendering").GetString()!;
            var documentType = e.GetProperty("DocumentType").GetString()!;
            await gate.WaitAsync(ct);
            try
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(corpus, file), ct);
                using var stream = new MemoryStream(bytes);
                var classification = await classifier.ClassifyAsync(stream, ct);
                var prepared = await preparer.PrepareAsync(stream, classification, ct);
                var response = await extractor.ExtractAsync(file, classification, prepared, ct);
                return (file, rendering, documentType, e.GetProperty("Facts"), response, (Exception?)null);
            }
            catch (Exception ex)
            {
                return (file, rendering, documentType, e.GetProperty("Facts"), (ExtractDocumentResponse?)null, ex);
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var (file, rendering, documentType, facts, response, error) in results)
        {
            if (response is null)
            {
                failures.Add($"{file}: {error!.GetType().Name}: {error.Message}");
                checks.Add(new Check(file, rendering, "(extraction failed)", false, "a result", error.Message));
                continue;
            }
            var expectedType = documentType switch
            {
                "loan-application" => LendingDocumentType.LoanApplication,
                "ucc1" => LendingDocumentType.Ucc1,
                "ucc3" => LendingDocumentType.Ucc3,
                _ => LendingDocumentType.LoanSecurityAgreement
            };
            detection.Add((file, rendering, response.DetectedBy == "keywords" && response.DocumentType == expectedType));
            checks.Add(new Check(file, rendering, "documentType", response.DocumentType == expectedType, expectedType.ToString(), response.DocumentType.ToString()));
            checks.AddRange(ExtractionScoring.Score(file, rendering, documentType,
                facts.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!), response.Extraction));
            output.WriteLine($"{file}: {response.Agent} ({response.Model}), {response.Usage.InputTokens} in / {response.Usage.OutputTokens} out" +
                             (response.Warnings.Count > 0 ? $" [{string.Join("; ", response.Warnings)}]" : ""));
        }

        output.WriteLine("");
        output.WriteLine($"Keyword detector: {detection.Count(d => d.Correct)}/{detection.Count} documents recognised from their text, with no fallback to the generic agent");
        foreach (var g in detection.GroupBy(d => d.Rendering).OrderBy(g => g.Key))
            output.WriteLine($"  {g.Key,-8} {g.Count(d => d.Correct)}/{g.Count()}");
        output.WriteLine("");
        output.WriteLine("Accuracy by rendering:");
        foreach (var g in checks.GroupBy(c => c.Rendering).OrderBy(g => g.Key))
            output.WriteLine($"  {g.Key,-8} {Percent(g.Count(c => c.Pass), g.Count())}  ({g.Count(c => c.Pass)}/{g.Count()})");
        output.WriteLine("Accuracy by field:");
        foreach (var g in checks.GroupBy(c => c.Field).OrderBy(g => g.Key))
            output.WriteLine($"  {g.Key,-24} {Percent(g.Count(c => c.Pass), g.Count())}  ({g.Count(c => c.Pass)}/{g.Count()})");
        output.WriteLine("Misses:");
        foreach (var c in checks.Where(c => !c.Pass).OrderBy(c => c.File).ThenBy(c => c.Field))
            output.WriteLine($"  {c.File} | {c.Field} | expected '{c.Expected}' | got '{c.Actual}'");

        var accuracy = checks.Count(c => c.Pass) / (double)Math.Max(1, checks.Count);
        output.WriteLine($"Overall: {Percent(checks.Count(c => c.Pass), checks.Count)} over {entries.Count} documents (threshold {threshold:P0})");
        Assert.True(failures.Count == 0, "extraction failed for: " + string.Join("; ", failures));
        Assert.True(accuracy >= threshold, $"accuracy {accuracy:P1} is below the {threshold:P0} threshold; see the output for misses");
    }

    private sealed class NoMeterFactory : System.Diagnostics.Metrics.IMeterFactory
    {
        public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) => new(options);
        public void Dispose() { }
    }

    private static string Percent(int n, int d) => (n / (double)Math.Max(1, d)).ToString("P0", CultureInfo.InvariantCulture).PadLeft(5);
}

using System.Text;
using DocIntake.Contracts;
using DocIntake.ScanService;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocIntake.Tests;

/// <summary>
/// Scans with a real ClamAV daemon. Skipped unless CLAMD_HOST is set, so it never runs in the normal build.
/// Run: <c>docker compose up -d clamav</c>, then
/// <c>CLAMD_HOST=localhost dotnet run --project tests/DocIntake.Tests -c Release -- -trait "Category=Integration"</c>
/// </summary>
public class ClamdIntegrationTests
{
    // The standard anti-malware test file: harmless, and every scanner detects it.
    private const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    private static ClamdClient Scanner()
    {
        var host = Environment.GetEnvironmentVariable("CLAMD_HOST");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(host), "set CLAMD_HOST to a running ClamAV daemon to run the integration tests");
        var port = int.TryParse(Environment.GetEnvironmentVariable("CLAMD_PORT"), out var p) ? p : 3310;
        return new ClamdClient(
            Options.Create(new ScanOptions { ClamdHost = host!, ClamdPort = port, TimeoutSeconds = 120 }),
            NullLogger<ClamdClient>.Instance);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task The_test_virus_is_found()
    {
        using var document = new MemoryStream(Encoding.ASCII.GetBytes(Eicar));

        var result = await Scanner().ScanAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(ScanOutcomes.Infected, result.Outcome);
        Assert.Contains("EICAR", result.Signature, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_clean_pdf_passes()
    {
        using var document = new MemoryStream(DocumentFixtures.TextPdf());

        var result = await Scanner().ScanAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(ScanOutcomes.Clean, result.Outcome);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task The_test_virus_inside_a_large_archive_is_found()
    {
        // The EICAR signature is a whole-file hash, so it only matches the file itself: put it in a zip that is many
        // 64 KB chunks long, which ClamAV unpacks, so the document really does cross chunk boundaries.
        var noise = new byte[2_000_000];
        new Random(1).NextBytes(noise);
        using var document = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(document, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("noise.bin", System.IO.Compression.CompressionLevel.NoCompression).Open())
                entry.Write(noise);
            using (var entry = zip.CreateEntry("eicar.com").Open())
                entry.Write(Encoding.ASCII.GetBytes(Eicar));
        }
        Assert.True(document.Length > 30 * ClamdClient.ChunkSize);

        var result = await Scanner().ScanAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(ScanOutcomes.Infected, result.Outcome);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task The_daemon_answers_a_ping()
    {
        Assert.True(await Scanner().PingAsync(TestContext.Current.CancellationToken));
    }
}

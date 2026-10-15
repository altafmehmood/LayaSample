using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DocIntake.Contracts;
using DocIntake.ScanService;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocIntake.Tests;

public class ClamdClientTests
{
    private static ClamdClient Scanner(int port, int timeoutSeconds = 10) => new(
        Options.Create(new ScanOptions { ClamdHost = "127.0.0.1", ClamdPort = port, TimeoutSeconds = timeoutSeconds }),
        NullLogger<ClamdClient>.Instance);

    private static byte[] Document(int bytes)
    {
        var data = new byte[bytes];
        new Random(7).NextBytes(data);
        return data;
    }

    [Fact]
    public async Task A_clean_document_is_streamed_in_chunks()
    {
        using var clamd = new FakeClamd { Reply = "stream: OK" };
        var data = Document(300_000);
        using var document = new MemoryStream(data);

        var result = await Scanner(clamd.Port).ScanAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(ScanOutcomes.Clean, result.Outcome);
        Assert.Equal(["zINSTREAM"], clamd.Commands);
        Assert.Equal(data.Length, clamd.Received);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), clamd.ReceivedHash);
        // Fixed-size chunks: memory per scan does not depend on the document.
        Assert.All(clamd.Chunks, c => Assert.True(c <= ClamdClient.ChunkSize));
        Assert.Equal(5, clamd.Chunks.Count);
    }

    [Fact]
    public async Task An_infected_document_reports_the_signature()
    {
        using var clamd = new FakeClamd { Reply = "stream: Eicar-Test-Signature FOUND" };
        using var document = new MemoryStream(Document(1000));

        var result = await Scanner(clamd.Port).ScanAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal((ScanOutcomes.Infected, "Eicar-Test-Signature"), (result.Outcome, result.Signature));
    }

    [Fact]
    public async Task A_document_clamd_refuses_has_no_verdict_rather_than_an_exception()
    {
        using var clamd = new FakeClamd { SizeLimit = 100_000 };
        using var document = new MemoryStream(Document(2_000_000));

        var result = await Scanner(clamd.Port).ScanAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(ScanOutcomes.Unavailable, result.Outcome);
        Assert.Contains("size limit", result.Error);
    }

    [Fact]
    public async Task An_unexpected_reply_is_not_a_clean_verdict()
    {
        using var clamd = new FakeClamd { Reply = "garbage" };
        using var document = new MemoryStream(Document(10));

        var result = await Scanner(clamd.Port).ScanAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(ScanOutcomes.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task A_scanner_that_is_not_there_has_no_verdict()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        using var document = new MemoryStream(Document(10));

        var result = await Scanner(port).ScanAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(ScanOutcomes.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task A_scanner_that_never_answers_times_out_into_no_verdict()
    {
        using var clamd = new FakeClamd { Silent = true };
        using var document = new MemoryStream(Document(10));

        var result = await Scanner(clamd.Port, timeoutSeconds: 1).ScanAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(ScanOutcomes.Unavailable, result.Outcome);
        Assert.Contains("no verdict within 1 s", result.Error);
    }

    [Fact]
    public async Task A_cancelled_request_is_not_mistaken_for_a_scanner_problem()
    {
        using var clamd = new FakeClamd { Silent = true };
        using var document = new MemoryStream(Document(10));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scanner(clamd.Port).ScanAsync(document, cts.Token));
    }

    [Fact]
    public async Task Ping_tells_a_live_scanner_from_a_dead_one()
    {
        using var clamd = new FakeClamd();
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var deadPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        Assert.True(await Scanner(clamd.Port).PingAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["zPING"], clamd.Commands);
        Assert.False(await Scanner(deadPort).PingAsync(TestContext.Current.CancellationToken));
    }
}

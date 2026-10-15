using System.Collections.Concurrent;
using System.Diagnostics;
using DocIntake.Contracts;
using DocIntake.ScanService;
using DocIntake.ServiceDefaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocIntake.Tests;

/// <summary>What a trace says about the calls between services: the client span names and the scan service's clamd span.</summary>
public class TraceNamingTests
{
    [Theory]
    [InlineData("http://localhost:3500/v1.0/invoke/docintake-document/method/documents/prepare", "POST docintake-document /documents/prepare")]
    [InlineData("http://localhost:3500/v1.0/invoke/docintake-scan/method/scan", "POST docintake-scan /scan")]
    [InlineData("http://localhost:3500/v1.0/invoke/docintake-extraction/method/extract", "POST docintake-extraction /extract")]
    [InlineData("https://api.openai.com/v1/chat/completions", "POST api.openai.com /v1/chat/completions")]
    public void A_client_span_names_the_service_and_the_operation(string url, string expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        Assert.Equal(expected, IntakeTelemetry.ClientSpanName(request));
    }

    [Fact]
    public void A_request_without_an_address_keeps_its_method_as_the_name()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, (Uri?)null);
        Assert.Equal("GET", IntakeTelemetry.ClientSpanName(request));
    }

    [Fact]
    public async Task The_scan_service_records_the_clamd_round_trip()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == IntakeTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(listener);

        using var clamd = new FakeClamd { Reply = "stream: Eicar-Test-Signature FOUND" };
        var scanner = new ClamdClient(
            Options.Create(new ScanOptions { ClamdHost = "127.0.0.1", ClamdPort = clamd.Port, TimeoutSeconds = 10 }),
            NullLogger<ClamdClient>.Instance);
        var data = new byte[70_000];
        new Random(3).NextBytes(data);
        using var document = new MemoryStream(data);

        var result = await scanner.ScanAsync(document, TestContext.Current.CancellationToken);

        Assert.Equal(ScanOutcomes.Infected, result.Outcome);
        var span = Assert.Single(spans, s => s.OperationName == "scan.clamd");
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal("clamd", span.GetTagItem("peer.service"));
        Assert.Equal((long)data.Length, span.GetTagItem("intake.scan.bytes"));
        Assert.Equal(ScanOutcomes.Infected, span.GetTagItem("intake.scan.result"));
    }
}

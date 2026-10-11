using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents.Extraction;
using LayaSample.Rendering;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LayaSample.Tests;

/// <summary>What one request leaves in its trace: the spans, their tags, and the failure on a rejected document.</summary>
public class TelemetryTests
{
    private sealed class Factory(ExtractionClients clients) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
                s.RemoveAll<IOcrEngine>();
                s.AddSingleton<IOcrEngine>(new FakeOcr());
                s.RemoveAll<ExtractionClients>();
                s.AddSingleton(clients);
            });
    }

    /// <summary>Sends the upload under a fresh trace id and returns the spans of that trace once the server span has ended.</summary>
    private static async Task<(HttpStatusCode Status, List<Activity> Spans)> Send(Factory factory, string path, byte[] file)
    {
        var spans = new ConcurrentBag<Activity>();
        var traceId = ActivityTraceId.CreateRandom();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == LayaTelemetry.SourceName || s.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if (a.TraceId == traceId) spans.Add(a); }
        };
        ActivitySource.AddActivityListener(listener);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(file), "file", "doc.pdf" } }
        };
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        var response = await factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        // The server span ends after the response is written.
        for (var i = 0; i < 40 && !spans.Any(s => s.Kind == ActivityKind.Server); i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        return (response.StatusCode, spans.ToList());
    }

    private static object? Tag(Activity a, string key) => a.GetTagItem(key);

    [Fact]
    public async Task An_extraction_leaves_the_stages_the_prompt_and_the_answer_on_its_trace()
    {
        var fake = FakeChatClient.Returning(Extracted.GenericRoot(LendingDocumentType.Ucc1, Extracted.Ucc1()));
        using var factory = new Factory(new ExtractionClients(fake, fake));

        var (status, spans) = await Send(factory, "/api/documents/extract", DocumentFixtures.TextPdf());

        Assert.Equal(HttpStatusCode.OK, status);
        var names = spans.Select(s => s.OperationName).ToList();
        foreach (var expected in new[] { "laya.buffer", "laya.classify", "laya.prepare", "laya.extraction", "laya.extract" })
            Assert.Contains(expected, names);

        var server = spans.Single(s => s.Kind == ActivityKind.Server);
        Assert.Equal("ok", Tag(server, "laya.outcome"));
        Assert.NotNull(Tag(server, "laya.document.kind"));
        Assert.NotNull(Tag(server, "laya.file.bytes"));

        var extract = spans.Single(s => s.OperationName == "laya.extract");
        Assert.Contains("You extract data from commercial lending documents", (string)Tag(extract, "laya.prompt.system")!);
        Assert.Contains("<document>", (string)Tag(extract, "laya.prompt.document")!);
        Assert.Contains("ucc1", (string)Tag(extract, "laya.extraction.result")!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1000L, Convert.ToInt64(Tag(extract, "laya.extraction.input_tokens")));
    }

    [Fact]
    public async Task A_rejected_document_marks_the_request_failed_with_the_reason()
    {
        var fake = FakeChatClient.Returning(Extracted.GenericRoot(LendingDocumentType.Ucc1, Extracted.Ucc1()));
        using var factory = new Factory(new ExtractionClients(fake, fake));

        var (status, spans) = await Send(factory, "/api/documents/extract", [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.Empty(fake.Calls);
        var server = spans.Single(s => s.Kind == ActivityKind.Server);
        Assert.Equal("rejected", Tag(server, "laya.outcome"));
        Assert.Equal(ActivityStatusCode.Error, server.Status);
        Assert.Contains(server.Events, e => e.Name == "exception");
    }
}

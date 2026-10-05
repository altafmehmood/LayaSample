using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents;
using LayaSample.Rendering;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LayaSample.Tests;

public class AnalyzeDocumentEndpointTests : IClassFixture<AnalyzeDocumentEndpointTests.Factory>
{
    public sealed class RecordingAgent(string route) : IDocumentAgent
    {
        public string Route { get; } = route;
        public Task<AgentResult> HandleAsync(DocumentClassification classification, PreparedDocument document, CancellationToken ct = default) =>
            Task.FromResult(new AgentResult(Route, "handled", "fake"));
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
                s.RemoveAll<IDocumentAgent>();
                s.RemoveAll<IOcrEngine>();
                s.AddSingleton<IOcrEngine>(new FakeOcr());
                foreach (var route in DocumentRoutes.All) s.AddSingleton<IDocumentAgent>(new RecordingAgent(route));
            });
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public AnalyzeDocumentEndpointTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<HttpResponseMessage> Post(byte[]? bytes, string fileName = "doc.bin", string query = "")
    {
        using var form = new MultipartFormDataContent();
        if (bytes is not null) form.Add(new ByteArrayContent(bytes), "file", fileName);
        return await _client.PostAsync("/api/documents/analyze" + query, form, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Rejects_missing_file()
    {
        var res = await Post(null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Rejects_unsupported_type()
    {
        var res = await Post("hello"u8.ToArray(), "notes.txt");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
    }

    [Fact]
    public async Task Rejects_oversized_file()
    {
        var res = await Post(new byte[20 * 1024 * 1024 + 1], "big.pdf");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    public static TheoryData<string, string, DocumentKind, PreparationStrategy, string> Samples => new()
    {
        { "form.pdf", "form", DocumentKind.FormPdf, PreparationStrategy.RenderToPng, DocumentRoutes.Form },
        { "signed.pdf", "annotated", DocumentKind.AnnotatedPdf, PreparationStrategy.RenderToPng, DocumentRoutes.Form },
        { "text.pdf", "text", DocumentKind.TextPdf, PreparationStrategy.Markdown, DocumentRoutes.Text },
        { "scan.pdf", "scan", DocumentKind.ScannedPdf, PreparationStrategy.Hybrid, DocumentRoutes.Vision },
        { "fax.tif", "tiff", DocumentKind.Image, PreparationStrategy.Hybrid, DocumentRoutes.Vision },
        { "e-invoice.pdf", "attachment", DocumentKind.TextPdf, PreparationStrategy.Markdown, DocumentRoutes.Text },
        { "book.xlsx", "xlsx", DocumentKind.Spreadsheet, PreparationStrategy.Markdown, DocumentRoutes.Spreadsheet },
        { "memo.docx", "docx", DocumentKind.WordDocument, PreparationStrategy.Markdown, DocumentRoutes.Text },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Classifies_prepares_and_routes(string fileName, string sample, DocumentKind kind, PreparationStrategy strategy, string route)
    {
        byte[] bytes = sample switch
        {
            "form" => DocumentFixtures.FilledFormPdf(),
            "annotated" => DocumentFixtures.AnnotatedPdf(),
            "text" => DocumentFixtures.TextPdf(),
            "scan" => DocumentFixtures.BlankPdf(),
            "tiff" => DocumentFixtures.FaxTiff(),
            "attachment" => DocumentFixtures.PdfWithXmlAttachment(),
            "xlsx" => DocumentFixtures.Xlsx(),
            _ => DocumentFixtures.Docx()
        };

        var res = await Post(bytes, fileName);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<AnalyzeDocumentResponse>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(kind, body.Classification.Kind);
        Assert.Equal(strategy, body.Classification.Strategy);
        Assert.Equal(route, body.Classification.Route);
        Assert.Equal("handled", body.Agent?.Status);
        Assert.Equal(route, body.Agent?.Agent);
        Assert.NotEmpty(body.Parts);
    }

    [Fact]
    public async Task Part_roles_and_sources_are_returned()
    {
        var res = await Post(DocumentFixtures.PdfWithXmlAttachment("invoice.xml"), "e-invoice.pdf", "?dispatch=false");

        var body = await res.Content.ReadFromJsonAsync<AnalyzeDocumentResponse>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Contains(body.Parts, p => p is { Role: PartRole.PageText, Source: null });
        Assert.Contains(body.Parts, p => p is { Role: PartRole.StructuredData, Source: "invoice.xml" });
        Assert.Equal("invoice.xml", Assert.Single(body.Classification.Attachments).Name);
    }

    [Fact]
    public async Task Dispatch_flag_skips_the_agent()
    {
        var res = await Post(DocumentFixtures.TextPdf(), "text.pdf", "?dispatch=false");

        var body = await res.Content.ReadFromJsonAsync<AnalyzeDocumentResponse>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Null(body.Agent);
    }

    [Fact]
    public async Task Strategy_query_parameter_is_ignored()
    {
        // The API always chooses the strategy from the content; an old client's ?strategy= changes nothing.
        var res = await Post(DocumentFixtures.TextPdf(), "text.pdf", "?strategy=RenderToPng&dispatch=false");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<AnalyzeDocumentResponse>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Equal(PreparationStrategy.Markdown, body.Classification.Strategy);
        Assert.All(body.Parts, p => Assert.Equal("text/markdown", p.MediaType));
    }

    [Fact]
    public async Task Png_parts_are_returned_as_base64()
    {
        var res = await Post(DocumentFixtures.Png(), "scan.png", "?dispatch=false");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var image = doc.RootElement.GetProperty("parts").EnumerateArray().First(p => p.GetProperty("role").GetString() == "PageImage");
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], Convert.FromBase64String(image.GetProperty("dataBase64").GetString()!)[..4]);
    }

    [Fact]
    public async Task Unavailable_ocr_is_a_server_error_not_a_bad_document()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IOcrEngine>();
            s.AddSingleton<IOcrEngine>(new FakeOcr(fail: true));
        }));
        using var form = new MultipartFormDataContent { { new ByteArrayContent(DocumentFixtures.BlankPdf()), "file", "scan.pdf" } };

        var res = await factory.CreateClient().PostAsync("/api/documents/analyze?dispatch=false", form, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task Errors_are_problem_details()
    {
        var res = await Post("hello"u8.ToArray(), "notes.txt");

        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(415, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Contains("unsupported document type", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Binary_data_can_be_left_out()
    {
        var res = await Post(DocumentFixtures.Png(), "scan.png", "?dispatch=false&includeData=false");

        var body = await res.Content.ReadFromJsonAsync<AnalyzeDocumentResponse>(Json, TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.Contains(body.Parts, p => p.Role == PartRole.PageImage);
        Assert.All(body.Parts, p => Assert.Null(p.DataBase64));
    }

    private static async Task<HttpResponseMessage> PostTo(HttpClient client, byte[] bytes, string fileName)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", fileName } };
        return await client.PostAsync("/api/documents/analyze?dispatch=false", form, TestContext.Current.CancellationToken);
    }

    private WebApplicationFactory<Program> WithBlockingOcr(BlockingOcr ocr, params (string Key, string Value)[] settings) =>
        _factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IOcrEngine>();
                s.AddSingleton<IOcrEngine>(ocr);
            });
        });

    [Fact]
    public async Task Requests_beyond_the_concurrency_limit_are_turned_away()
    {
        var ocr = new BlockingOcr();
        using var factory = WithBlockingOcr(ocr, ("DocumentAnalysis:MaxConcurrentAnalyses", "1"), ("DocumentAnalysis:MaxQueuedAnalyses", "0"));
        var client = factory.CreateClient();

        var first = PostTo(client, DocumentFixtures.BlankPdf(), "scan.pdf");
        await ocr.Entered.WaitAsync(TestContext.Current.CancellationToken); // the first request holds the only slot
        var second = await PostTo(client, DocumentFixtures.TextPdf(), "text.pdf");
        ocr.Release();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
    }

    [Fact]
    public async Task Analysis_that_runs_too_long_times_out()
    {
        var ocr = new BlockingOcr();
        using var factory = WithBlockingOcr(ocr, ("DocumentAnalysis:RequestTimeoutSeconds", "1"));

        var res = await PostTo(factory.CreateClient(), DocumentFixtures.BlankPdf(), "scan.pdf");

        Assert.Equal(HttpStatusCode.GatewayTimeout, res.StatusCode);
    }
}

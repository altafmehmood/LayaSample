using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents;
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
                foreach (var route in DocumentRoutes.All) s.AddSingleton<IDocumentAgent>(new RecordingAgent(route));
            });
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly HttpClient _client;
    public AnalyzeDocumentEndpointTests(Factory factory) => _client = factory.CreateClient();

    private async Task<HttpResponseMessage> Post(byte[]? bytes, string fileName = "doc.bin", string query = "")
    {
        using var form = new MultipartFormDataContent();
        if (bytes is not null) form.Add(new ByteArrayContent(bytes), "file", fileName);
        return await _client.PostAsync("/api/documents/analyze" + query, form);
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

    [Fact]
    public async Task Rejects_png_strategy_for_spreadsheet()
    {
        var res = await Post(DocumentFixtures.Xlsx(), "a.xlsx", "?strategy=RenderToPng");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    public static TheoryData<string, string, DocumentKind, PreparationStrategy, string> Samples => new()
    {
        { "form.pdf", "form", DocumentKind.FormPdf, PreparationStrategy.RenderToPng, DocumentRoutes.Form },
        { "text.pdf", "text", DocumentKind.TextPdf, PreparationStrategy.Markdown, DocumentRoutes.Text },
        { "scan.pdf", "scan", DocumentKind.ScannedPdf, PreparationStrategy.RenderToPng, DocumentRoutes.Vision },
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
            "text" => DocumentFixtures.TextPdf(),
            "scan" => DocumentFixtures.BlankPdf(),
            "xlsx" => DocumentFixtures.Xlsx(),
            _ => DocumentFixtures.Docx()
        };

        var res = await Post(bytes, fileName);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<AnalyzeDocumentResponse>(Json);
        Assert.NotNull(body);
        Assert.Equal(kind, body.Classification.Kind);
        Assert.Equal(strategy, body.AppliedStrategy);
        Assert.Equal(route, body.Classification.Route);
        Assert.Equal("handled", body.Agent?.Status);
        Assert.Equal(route, body.Agent?.Agent);
        Assert.NotEmpty(body.Parts);
    }

    [Fact]
    public async Task Strategy_override_and_dispatch_flag_are_honoured()
    {
        var res = await Post(DocumentFixtures.TextPdf(), "text.pdf", "?strategy=RenderToPng&dispatch=false");

        var body = await res.Content.ReadFromJsonAsync<AnalyzeDocumentResponse>(Json);
        Assert.NotNull(body);
        Assert.Equal(PreparationStrategy.RenderToPng, body.AppliedStrategy);
        Assert.All(body.Parts, p => Assert.Equal("image/png", p.MediaType));
        Assert.Null(body.Agent);
    }
}

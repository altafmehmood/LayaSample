using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LayaSample.Api.Models;
using LayaSample.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LayaSample.Tests;

/// <summary>The API configured with a renderer service, as deployed: no rendering in the API process.</summary>
public class RemoteRenderingEndpointTests(RendererTests.RendererFactory renderer) : IClassFixture<RendererTests.RendererFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ThrowingHandler(HttpRequestError error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException(error, $"simulated {error}");
    }

    /// <summary>The API with RendererUrl set, its renderer calls going to <paramref name="handler"/>.</summary>
    private static WebApplicationFactory<Program> Api(Func<HttpMessageHandler> handler) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DocumentAnalysis:RendererUrl", "http://renderer")
            .ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
                s.AddHttpClient(RemotePageRasterizer.HttpClientName).ConfigurePrimaryHttpMessageHandler(handler);
            }));

    private static async Task<HttpResponseMessage> Post(HttpClient client, byte[] bytes, string fileName)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", fileName } };
        return await client.PostAsync("/api/documents/analyze?dispatch=false", form, Ct);
    }

    public static TheoryData<string, DocumentKind, PartRole[]> Samples => new()
    {
        { "scan.pdf", DocumentKind.ScannedPdf, [PartRole.PageImage, PartRole.OcrText] },
        { "fax.tif", DocumentKind.Image, [PartRole.PageImage, PartRole.OcrText, PartRole.PageImage, PartRole.OcrText] },
        { "form.pdf", DocumentKind.FormPdf, [PartRole.PageImage, PartRole.StructuredData] },
        { "mixed.pdf", DocumentKind.MixedPdf, [PartRole.PageText, PartRole.PageImage, PartRole.OcrText] },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Documents_are_rendered_by_the_renderer_service(string fileName, DocumentKind kind, PartRole[] roles)
    {
        using var api = Api(() => renderer.Server.CreateHandler());
        byte[] bytes = fileName switch
        {
            "scan.pdf" => DocumentFixtures.BlankPdf(),
            "fax.tif" => DocumentFixtures.FaxTiff(),
            "form.pdf" => DocumentFixtures.FilledFormPdf(),
            _ => DocumentFixtures.MixedPdf()
        };

        var res = await Post(api.CreateClient(), bytes, fileName);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<AnalyzeDocumentResponse>(Json, Ct);
        Assert.Equal(kind, body!.Classification.Kind);
        Assert.Equal(roles, body.Parts.Select(p => p.Role));
    }

    [Fact]
    public async Task Unreachable_renderer_fails_only_documents_that_need_it()
    {
        using var api = Api(() => new ThrowingHandler(HttpRequestError.ConnectionError));
        var client = api.CreateClient();

        var scan = await Post(client, DocumentFixtures.BlankPdf(), "scan.pdf");
        var text = await Post(client, DocumentFixtures.TextPdf(), "text.pdf");
        var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, scan.StatusCode);
        Assert.NotNull(scan.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, text.StatusCode);
        using var health = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Degraded", health.RootElement.GetProperty("checks").GetProperty("renderer").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Renderer_crashing_on_a_document_is_a_bad_gateway()
    {
        using var api = Api(() => new ThrowingHandler(HttpRequestError.ResponseEnded));

        var res = await Post(api.CreateClient(), DocumentFixtures.BlankPdf(), "scan.pdf");

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
    }

    [Fact]
    public async Task Readiness_checks_the_renderer_instead_of_local_ocr()
    {
        using var api = Api(() => renderer.Server.CreateHandler());

        var res = await api.CreateClient().GetAsync("/health/ready", Ct);

        using var health = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        var checks = health.RootElement.GetProperty("checks");
        Assert.True(checks.TryGetProperty("renderer", out var check));
        Assert.NotEqual("Unhealthy", check.GetProperty("status").GetString());
        Assert.False(checks.TryGetProperty("ocr", out _));
    }

    [Fact]
    public void Production_without_a_renderer_refuses_to_start()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment(Environments.Production)
            .ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            }));

        var ex = Assert.ThrowsAny<Exception>(() => api.CreateClient());
        Assert.Contains("RendererUrl", ex.ToString());
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocIntake.Api.Services.Documents;
using DocIntake.Contracts;
using DocIntake.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocIntake.Tests;

/// <summary>The gateway with a document service behind it, as deployed: the gateway reads no document itself.</summary>
public class GatewayDocumentServiceTests(DefaultDocumentService documentService) : IClassFixture<DefaultDocumentService>
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

    /// <summary>The gateway with its document service calls going to <paramref name="handler"/>.</summary>
    private static WebApplicationFactory<Program> Gateway(Func<HttpMessageHandler> handler) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("DocumentAnalysis:DocumentServiceUrl", "http://document")
            .ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
                s.AddHttpClient(DocumentServiceClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(handler);
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
    public async Task Documents_are_analysed_by_the_document_service(string fileName, DocumentKind kind, PartRole[] roles)
    {
        using var api = Gateway(() => documentService.Server.CreateHandler());
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
    public async Task An_unreachable_document_service_fails_every_upload_but_not_feedback_analysis()
    {
        using var api = Gateway(() => new ThrowingHandler(HttpRequestError.ConnectionError));
        var client = api.CreateClient();

        // Classification happens in the document service too, so a text PDF fails like a scan: unlike before the split.
        var scan = await Post(client, DocumentFixtures.BlankPdf(), "scan.pdf");
        var text = await Post(client, DocumentFixtures.TextPdf(), "text.pdf");
        var ready = await client.GetAsync("/health/ready", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, scan.StatusCode);
        Assert.NotNull(scan.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, text.StatusCode);
        Assert.NotNull(text.Headers.RetryAfter);
        using var health = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Degraded", health.RootElement.GetProperty("checks").GetProperty("document-service").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_document_service_crashing_on_a_document_is_a_bad_gateway()
    {
        using var api = Gateway(() => new ThrowingHandler(HttpRequestError.ResponseEnded));

        var res = await Post(api.CreateClient(), DocumentFixtures.BlankPdf(), "scan.pdf");

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
    }

    [Fact]
    public async Task Readiness_checks_the_document_service_and_the_gateway_has_no_ocr_of_its_own()
    {
        using var api = Gateway(() => documentService.Server.CreateHandler());

        var res = await api.CreateClient().GetAsync("/health/ready", Ct);

        using var health = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        var checks = health.RootElement.GetProperty("checks");
        Assert.True(checks.TryGetProperty("document-service", out var check));
        Assert.NotEqual("Unhealthy", check.GetProperty("status").GetString());
        Assert.False(checks.TryGetProperty("ocr", out _));
    }

    [Fact]
    public void Production_without_a_document_service_refuses_to_start()
    {
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment(Environments.Production)
            .ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
            }));

        var ex = Assert.ThrowsAny<Exception>(() => api.CreateClient());
        Assert.Contains("DocumentServiceUrl", ex.ToString());
    }
}

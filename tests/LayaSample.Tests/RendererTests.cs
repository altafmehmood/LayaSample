using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LayaSample.Renderer;
using LayaSample.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkiaSharp;

namespace LayaSample.Tests;

/// <summary>The renderer service, and the client the API uses to call it.</summary>
public class RendererTests : IClassFixture<RendererTests.RendererFactory>
{
    public sealed class RendererFactory : WebApplicationFactory<RendererEntryPoint>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<IOcrEngine>();
                s.AddSingleton<IOcrEngine>(new FakeOcr());
            });
    }

    private readonly RendererFactory _renderer;

    public RendererTests(RendererFactory renderer) => _renderer = renderer;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The API's client, wired to the in-memory renderer (or any handler standing in for it).</summary>
    private static RemotePageRasterizer Remote(HttpMessageHandler handler) => new(new StubHttpClientFactory(handler));

    private RemotePageRasterizer Remote() => Remote(_renderer.Server.CreateHandler());

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://renderer/") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage ProblemResponse(HttpStatusCode status, string? type = null) =>
        new(status) { Content = JsonContent.Create(new { status = (int)status, type, detail = "from renderer" }) };

    [Fact]
    public async Task Pdf_pages_are_rendered_to_png()
    {
        var pages = await Remote().RenderAsync(new RenderJob(DocumentFixtures.TextPdf(), MediaTypes.Pdf, [new(1, 72), new(2, 72)]), Ct);

        Assert.Equal([1, 2], pages.Select(p => p.Number));
        Assert.All(pages, p => Assert.Equal([0x89, 0x50, 0x4E, 0x47], p.Png[..4]));
        Assert.All(pages, p => Assert.Null(p.OcrText));
    }

    [Fact]
    public async Task Fax_pages_come_back_greyscale_with_ocr_text()
    {
        var pages = await Remote().RenderAsync(
            new RenderJob(DocumentFixtures.FaxTiff(pages: 2), MediaTypes.Tiff, [new(1, Grayscale: true, Ocr: true), new(2, Grayscale: true, Ocr: true)]), Ct);

        Assert.All(pages, p => Assert.Equal(SKColorType.Gray8, SKCodec.Create(new MemoryStream(p.Png)).Info.ColorType));
        Assert.All(pages, p => Assert.Equal("recognised text", p.OcrText));
    }

    [Fact]
    public async Task Tiff_pages_are_inspected()
    {
        var pages = await Remote().InspectImageAsync(DocumentFixtures.FaxTiff(pages: 3), MediaTypes.Tiff, maxFrames: 10, Ct);

        Assert.Equal(3, pages.Count);
        Assert.All(pages, p => Assert.True(p.IsFax));
    }

    [Fact]
    public async Task Unreadable_documents_are_reported_as_invalid_data()
    {
        var truncatedPng = DocumentFixtures.Png()[..40];

        await Assert.ThrowsAsync<InvalidDataException>(() => Remote().InspectImageAsync(truncatedPng, MediaTypes.Png, 1, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => Remote().RenderAsync(new RenderJob(truncatedPng, MediaTypes.Png, [new(1)]), Ct));
    }

    [Fact]
    public async Task Missing_ocr_models_keep_their_meaning_across_the_boundary()
    {
        using var renderer = _renderer.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IOcrEngine>();
            s.AddSingleton<IOcrEngine>(new FakeOcr(fail: true));
        }));

        await Assert.ThrowsAsync<OcrUnavailableException>(() =>
            Remote(renderer.Server.CreateHandler()).RenderAsync(new RenderJob(DocumentFixtures.Png(), MediaTypes.Png, [new(1, Ocr: true)]), Ct));
    }

    [Fact]
    public async Task Renderer_rejects_oversized_documents_and_bad_jobs()
    {
        using var renderer = _renderer.WithWebHostBuilder(b => b
            .UseSetting("DocumentAnalysis:MaxDocumentBytes", "1000")
            .UseSetting("DocumentAnalysis:MaxPagesPerJob", "1"));
        var client = renderer.CreateClient();

        async Task<HttpStatusCode> Render(byte[] document, object job)
        {
            using var form = new MultipartFormDataContent
            {
                { new ByteArrayContent(document), "document", "document" },
                { new StringContent(JsonSerializer.Serialize(job, JsonSerializerOptions.Web)), "job" }
            };
            return (await client.PostAsync("/render", form, Ct)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Render(new byte[2000], new RenderRequest(MediaTypes.Pdf, [new(1)])));
        Assert.Equal(HttpStatusCode.BadRequest, await Render(new byte[10], new RenderRequest(MediaTypes.Pdf, [new(1), new(2)])));
        Assert.Equal(HttpStatusCode.BadRequest, await Render(new byte[10], new RenderRequest("text/plain", [new(1)])));
        Assert.Equal(HttpStatusCode.BadRequest, await Render(new byte[10], new RenderRequest(MediaTypes.Pdf, [new(0)])));
    }

    [Fact]
    public async Task Huge_pages_are_rendered_within_the_pixel_limit()
    {
        var rasterizer = new LocalPageRasterizer(new FakeOcr(), Microsoft.Extensions.Options.Options.Create(new RenderingOptions()));

        // 200 x 200 inches at 300 dpi would be 3.6 billion pixels.
        var page = Assert.Single(await rasterizer.RenderAsync(new RenderJob(DocumentFixtures.HugePagePdf(), MediaTypes.Pdf, [new(1, 300)]), Ct));

        var info = SKCodec.Create(new MemoryStream(page.Png)).Info;
        Assert.InRange((long)info.Width * info.Height, 1, 60_000_000);
    }

    [Fact]
    public async Task Unreachable_renderer_is_unavailable()
    {
        var remote = Remote(new StubHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")));

        await Assert.ThrowsAsync<RendererUnavailableException>(() => remote.RenderAsync(new RenderJob([1], MediaTypes.Pdf, [new(1)]), Ct));
        Assert.Null(await remote.GetReadinessAsync(Ct));
    }

    [Fact]
    public async Task Renderer_dying_mid_request_is_a_rendering_failure()
    {
        var remote = Remote(new StubHandler(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset")));

        await Assert.ThrowsAsync<RenderingFailedException>(() => remote.RenderAsync(new RenderJob([1], MediaTypes.Pdf, [new(1)]), Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, null, typeof(InvalidDataException))]
    [InlineData(HttpStatusCode.ServiceUnavailable, OcrUnavailableException.ProblemType, typeof(OcrUnavailableException))]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, typeof(RendererUnavailableException))]
    [InlineData(HttpStatusCode.InternalServerError, null, typeof(RenderingFailedException))]
    public async Task Renderer_errors_map_to_exceptions(HttpStatusCode status, string? type, Type expected)
    {
        var remote = Remote(new StubHandler(_ => ProblemResponse(status, type)));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => remote.RenderAsync(new RenderJob([1], MediaTypes.Pdf, [new(1)]), Ct));
        Assert.IsType(expected, ex);
    }
}

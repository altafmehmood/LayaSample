using DocIntake.Api.Services.Documents;
using DocIntake.DocumentService;
using DocIntake.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocIntake.Tests;

/// <summary>The document service hosted in memory, with OCR replaced so no ONNX models are loaded.</summary>
public class DocumentServiceFactory(IOcrEngine? ocr, params (string Key, string Value)[] settings)
    : WebApplicationFactory<DocumentServiceEntryPoint>
{

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IOcrEngine>();
            s.AddSingleton(ocr ?? new FakeOcr());
        });
    }
}

/// <summary>For <c>IClassFixture</c>: the service as it is by default.</summary>
public sealed class DefaultDocumentService() : DocumentServiceFactory(null);

public static class DocumentServiceHost
{
    /// <summary>A document service that lives as long as the gateway it is attached to, and is only started when first called.</summary>
    private sealed class Handle(IOcrEngine? ocr, (string Key, string Value)[] settings) : IDisposable
    {
        private readonly Lazy<DocumentServiceFactory> _factory = new(() => new DocumentServiceFactory(ocr, settings));

        public HttpMessageHandler Handler => _factory.Value.Server.CreateHandler();

        public void Dispose()
        {
            if (_factory.IsValueCreated) _factory.Value.Dispose();
        }
    }

    /// <summary>
    /// Points the gateway's document service client at a new in-memory document service, as deployed: every upload is
    /// classified and prepared there, over HTTP, and the gateway has no parser of its own.
    /// </summary>
    /// <param name="ocr">The OCR engine the document service uses; a fake that recognises "recognised text" by default.</param>
    /// <param name="settings">Settings of the document service (not the gateway), such as <c>DocumentAnalysis:MaxPdfPages</c>.</param>
    public static IServiceCollection UseDocumentService(this IServiceCollection services, IOcrEngine? ocr = null,
        params (string Key, string Value)[] settings)
    {
        services.AddSingleton(_ => new Handle(ocr, settings));
        services.AddHttpClient(DocumentServiceClient.HttpClientName)
            .ConfigureHttpClient(c => c.BaseAddress ??= new Uri("http://document/"))
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<Handle>().Handler);
        // Production refuses to start without a document service URL; the address is never dialled.
        services.PostConfigure<DocumentGatewayOptions>(o => o.DocumentServiceUrl ??= "http://document");
        return services;
    }
}

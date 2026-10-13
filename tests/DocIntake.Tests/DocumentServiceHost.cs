using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using DocIntake.DocumentService;
using DocIntake.ExtractionService;
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

/// <summary>
/// Writes the caller's trace context on the request, as a real connection does. The in-memory handler skips the HTTP
/// diagnostics layer that normally adds it, so without this a call to a service would start a trace of its own.
/// </summary>
internal sealed class TraceContextHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (System.Diagnostics.Activity.Current is { Id: { } id } && !request.Headers.Contains("traceparent"))
            request.Headers.Add("traceparent", id);
        return base.SendAsync(request, ct);
    }
}

public static class DocumentServiceHost
{
    /// <summary>A document service that lives as long as the gateway it is attached to, and is only started when first called.</summary>
    private sealed class Handle(IOcrEngine? ocr, (string Key, string Value)[] settings) : IDisposable
    {
        private readonly Lazy<DocumentServiceFactory> _factory = new(() => new DocumentServiceFactory(ocr, settings));

        public HttpMessageHandler Handler => new TraceContextHandler(_factory.Value.Server.CreateHandler());

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

    /// <summary>An extraction service that lives as long as the gateway it is attached to, and is only started when first called.</summary>
    private sealed class ExtractionHandle(ExtractionClients clients, (string Key, string Value)[] settings) : IDisposable
    {
        private readonly Lazy<ExtractionServiceFactory> _factory = new(() => new ExtractionServiceFactory(clients, settings));

        public HttpMessageHandler Handler => new TraceContextHandler(_factory.Value.Server.CreateHandler());

        public void Dispose()
        {
            if (_factory.IsValueCreated) _factory.Value.Dispose();
        }
    }

    /// <summary>
    /// Points the gateway's extraction service client at a new in-memory extraction service whose model calls go to
    /// <paramref name="clients"/> (a fake chat client), as deployed: the gateway holds no key and no model.
    /// </summary>
    /// <param name="settings">Settings of the extraction service (not the gateway), such as <c>Extraction:MaxImagePages</c>.</param>
    public static IServiceCollection UseExtractionService(this IServiceCollection services, ExtractionClients clients,
        params (string Key, string Value)[] settings)
    {
        services.AddSingleton(_ => new ExtractionHandle(clients, settings));
        services.AddHttpClient(ExtractionServiceClient.HttpClientName)
            .ConfigureHttpClient(c => c.BaseAddress ??= new Uri("http://extraction/"))
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<ExtractionHandle>().Handler);
        services.PostConfigure<ExtractionGatewayOptions>(o => o.ServiceUrl ??= "http://extraction");
        return services;
    }
}

/// <summary>The extraction service hosted in memory, with the model calls going to fake chat clients.</summary>
public class ExtractionServiceFactory(ExtractionClients clients, params (string Key, string Value)[] settings)
    : WebApplicationFactory<ExtractionServiceEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<ExtractionClients>();
            s.AddSingleton(clients);
        });
    }
}

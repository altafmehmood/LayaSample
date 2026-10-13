using DocIntake.Api.Services;
using DocIntake.Api.Services.Decisions;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using DocIntake.Contracts;
using DocIntake.ServiceDefaults;

namespace DocIntake.Api;

/// <summary>
/// How the gateway reaches the three services behind it: an options class, an HTTP client with the service's address, a typed
/// client and a readiness check each. The gateway reads no document, calls no model provider and runs no model itself.
/// </summary>
internal static class ServiceClients
{
    /// <summary>
    /// Untrusted bytes are read only inside the document service: classification, preparation, rendering and OCR all happen
    /// there. Required: the gateway refuses to start without it.
    /// </summary>
    public static IServiceCollection AddDocumentServiceClient(this IServiceCollection services, IConfiguration configuration, IHealthChecksBuilder health)
    {
        services.AddOptions<DocumentGatewayOptions>()
            .Bind(configuration.GetSection(DocumentGatewayOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        var options = configuration.GetSection(DocumentGatewayOptions.Section).Get<DocumentGatewayOptions>() ?? new();
        services.AddServiceHttpClient(DocumentServiceClient.HttpClientName, options.DocumentServiceUrl);
        services.AddSingleton<DocumentServiceClient>();
        services.AddSingleton<IDocumentClassifier>(sp => sp.GetRequiredService<DocumentServiceClient>());
        services.AddSingleton<IDocumentPreparer>(sp => sp.GetRequiredService<DocumentServiceClient>());
        health.AddCheck<DocumentServiceHealthCheck>("document-service", tags: [HealthReporting.Ready], timeout: TimeSpan.FromSeconds(5));
        return services;
    }

    /// <summary>The model provider's key and the only route to it are in the extraction service. Without a URL, extraction answers 503.</summary>
    public static IServiceCollection AddExtractionServiceClient(this IServiceCollection services, IConfiguration configuration, IHealthChecksBuilder health)
    {
        services.AddOptions<ExtractionGatewayOptions>()
            .Bind(configuration.GetSection(ExtractionGatewayOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        var options = configuration.GetSection(ExtractionGatewayOptions.Section).Get<ExtractionGatewayOptions>() ?? new();
        services.AddServiceHttpClient(ExtractionServiceClient.HttpClientName, options.ServiceUrl);
        services.AddSingleton<ExtractionServiceClient>();
        services.AddSingleton<IDocumentExtractor, DocumentExtractor>();
        health.AddCheck<ExtractionServiceHealthCheck>("extraction-service", tags: [HealthReporting.Ready], timeout: TimeSpan.FromSeconds(5));
        return services;
    }

    /// <summary>Laya, the feedback model, runs in the decisions service. Without a URL, feedback analysis answers 503.</summary>
    public static IServiceCollection AddDecisionsServiceClient(this IServiceCollection services, IConfiguration configuration, IHealthChecksBuilder health)
    {
        services.AddOptions<DecisionsGatewayOptions>()
            .Bind(configuration.GetSection(DecisionsGatewayOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        var options = configuration.GetSection(DecisionsGatewayOptions.Section).Get<DecisionsGatewayOptions>() ?? new();
        services.AddServiceHttpClient(DecisionsServiceClient.HttpClientName, options.ServiceUrl);
        services.AddSingleton<DecisionsServiceClient>();
        health.AddCheck<DecisionsServiceHealthCheck>("decisions-service", tags: [HealthReporting.Ready], timeout: TimeSpan.FromSeconds(5));
        return services;
    }

    private static void AddServiceHttpClient(this IServiceCollection services, string name, string? url) =>
        services.AddHttpClient(name, c =>
        {
            if (!string.IsNullOrWhiteSpace(url))
                c.BaseAddress = new Uri(url.TrimEnd('/') + "/");
            // Bounded by the request's own token instead, which carries the request timeout.
            c.Timeout = Timeout.InfiniteTimeSpan;
        });
}

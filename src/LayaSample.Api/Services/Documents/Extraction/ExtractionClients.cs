using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Extensions.AI;
using OpenAI;

namespace LayaSample.Api.Services.Documents.Extraction;

/// <summary>A failure to extract that maps to a specific HTTP status.</summary>
public sealed class ExtractionException(int statusCode, string message, int? retryAfterSeconds = null, bool isMisconfiguration = false, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
    public bool IsMisconfiguration { get; } = isMisconfiguration;
}

/// <summary>The chat clients the agents call; both null while no API key is configured.</summary>
public sealed record ExtractionClients(IChatClient? Vision, IChatClient? Text)
{
    public bool IsConfigured => Vision is not null && Text is not null;
}

/// <summary>Translates one provider's SDK exceptions into <see cref="ExtractionException"/>.</summary>
public interface IExtractionErrorMapper
{
    ExtractionException? Map(Exception ex);
}

public sealed class OpenAiExtractionErrorMapper : IExtractionErrorMapper
{
    public ExtractionException? Map(Exception ex)
    {
        if (ex is not ClientResultException cre)
            return null;

        var retryAfter = cre.GetRawResponse()?.Headers.TryGetValue("retry-after", out var value) == true && int.TryParse(value, out var s) ? s : (int?)null;
        return cre.Status switch
        {
            (int)HttpStatusCode.TooManyRequests => new ExtractionException(StatusCodes.Status503ServiceUnavailable, "extraction is busy; retry later", retryAfter ?? 30, inner: ex),
            (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden =>
                new ExtractionException(StatusCodes.Status503ServiceUnavailable, "extraction is unavailable", isMisconfiguration: true, inner: ex),
            (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.RequestEntityTooLarge or (int)HttpStatusCode.UnprocessableEntity =>
                new ExtractionException(StatusCodes.Status422UnprocessableEntity, "the document is too large or not supported for extraction", inner: ex),
            _ => new ExtractionException(StatusCodes.Status502BadGateway, "the extraction service failed", inner: ex)
        };
    }
}

/// <summary>The one place a provider's client is built. Everything else depends only on <see cref="IChatClient"/>.</summary>
public static class ExtractionClientFactory
{
    public static ExtractionClients Create(ExtractionOptions options)
    {
        var key = options.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return new ExtractionClients(null, null);

        var clientOptions = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            RetryPolicy = new ClientRetryPolicy(options.MaxRetries)
        };
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
            clientOptions.Endpoint = new Uri(options.Endpoint);

        var openAi = new OpenAIClient(new ApiKeyCredential(key), clientOptions);
        // Chat Completions requests are not stored by OpenAI (store defaults to false).
        return new ExtractionClients(
            openAi.GetChatClient(options.VisionModel).AsIChatClient(),
            openAi.GetChatClient(options.TextModel).AsIChatClient());
    }
}

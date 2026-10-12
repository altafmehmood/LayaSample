using DocIntake.ServiceDefaults;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.AI;
using DocIntake.Contracts;
using OpenAI;

namespace DocIntake.Api.Services.Documents.Extraction;

/// <summary>Counts and records the SDK's repeated attempts, which are otherwise invisible except as extra latency.</summary>
public sealed class RecordingRetryPolicy(int maxRetries, DocumentMetrics? metrics) : ClientRetryPolicy(maxRetries)
{
    // Only the synchronous method is overridden: the base class's asynchronous one calls it, so a retry is recorded once on
    // both paths. Overriding both and calling one from the other recurses until the stack overflows and the process dies.
    protected override bool ShouldRetry(PipelineMessage message, Exception? exception)
    {
        var retry = base.ShouldRetry(message, exception);
        if (retry)
        {
            var reason = exception is not null ? exception.GetType().Name : ((int?)message.Response?.Status)?.ToString() ?? "unknown";
            metrics?.RecordLlmRetry(reason);
            Activity.Current?.AddEvent(new ActivityEvent("llm-retry", tags: new ActivityTagsCollection { ["intake.llm.retry.reason"] = reason }));
        }
        return retry;
    }
}

public sealed record ExtractionClients(IChatClient? Vision, IChatClient? Text)
{
    public bool IsConfigured => Vision is not null && Text is not null;
}

/// <summary>Translates one provider's SDK exceptions into <see cref="IntakeFailureException"/>.</summary>
public interface IExtractionErrorMapper
{
    IntakeFailureException? Map(Exception ex);
}

public sealed class OpenAiExtractionErrorMapper : IExtractionErrorMapper
{
    public IntakeFailureException? Map(Exception ex)
    {
        if (ex is not ClientResultException cre)
            return null;

        var retryAfter = cre.GetRawResponse()?.Headers.TryGetValue("retry-after", out var value) == true && int.TryParse(value, out var s) ? s : (int?)null;
        return cre.Status switch
        {
            (int)HttpStatusCode.TooManyRequests => new IntakeFailureException(FailureKind.Busy, "extraction is busy; retry later", retryAfter ?? 30, ex),
            (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden =>
                new IntakeFailureException(FailureKind.Misconfigured, "extraction is unavailable", inner: ex),
            (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.RequestEntityTooLarge or (int)HttpStatusCode.UnprocessableEntity =>
                new IntakeFailureException(FailureKind.Declined, "the document is too large or not supported for extraction", inner: ex),
            _ => new IntakeFailureException(FailureKind.DependencyFailed, "the extraction service failed", inner: ex)
        };
    }
}

/// <summary>The one place a provider's client is built. Everything else depends only on <see cref="IChatClient"/>.</summary>
public static class ExtractionClientFactory
{
    public static ExtractionClients Create(ExtractionOptions options, DocumentMetrics? metrics = null)
    {
        var key = options.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return new ExtractionClients(null, null);

        var clientOptions = new OpenAIClientOptions
        {
            NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            RetryPolicy = new RecordingRetryPolicy(options.MaxRetries, metrics)
        };
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
            clientOptions.Endpoint = new Uri(options.Endpoint);

        var openAi = new OpenAIClient(new ApiKeyCredential(key), clientOptions);
        // Chat Completions requests are not stored by OpenAI (store defaults to false).
        return new ExtractionClients(
            WithTelemetry(openAi.GetChatClient(options.VisionModel).AsIChatClient()),
            WithTelemetry(openAi.GetChatClient(options.TextModel).AsIChatClient()));
    }

    // Gen-AI spans (model, token counts, latency). Prompts and responses are not recorded: they hold the documents.
    private static IChatClient WithTelemetry(IChatClient client) =>
        client.AsBuilder().UseOpenTelemetry(sourceName: IntakeTelemetry.GenAiSourceName).Build();
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocIntake.Contracts;
using DocIntake.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocIntake.Api.Services.Documents.Extraction;

/// <summary>
/// The gateway's way to the extraction service, the only process that holds the model provider's key. What the service
/// answers comes back as the failure it was there: the same <see cref="FailureKind"/>, the same retry hint and the model
/// calls made before it, so the gateway records the usage whether or not the extraction worked. A service that cannot be
/// reached or is saturated is <see cref="FailureKind.DependencyUnavailable"/>. Nothing is retried: a model call costs money.
/// </summary>
public sealed class ExtractionServiceClient(IHttpClientFactory httpClientFactory, IOptions<ExtractionGatewayOptions> options)
{
    /// <summary>Name of the <see cref="HttpClient"/> configured with the extraction service's base address.</summary>
    public const string HttpClientName = "extraction-service";

    /// <summary>How long the answer to "can you extract?" is trusted. Every extract request asks, before any work is done.</summary>
    private static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(5);

    private (DateTimeOffset At, bool Configured)? _status;

    public async Task<ExtractAnswer> ExtractAsync(ExtractRequest request, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("extract", request, IntakeJson.Options, ct);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            throw new IntakeFailureException(FailureKind.DependencyUnavailable, "the extraction service is unreachable; retry later",
                retryAfterSeconds: 10, inner: ex);
        }
        // Connected, then lost the connection: the service most likely died on this request.
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new IntakeFailureException(FailureKind.DependencyFailed, "the extraction service failed", inner: ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return await response.Content.ReadFromJsonAsync<ExtractAnswer>(IntakeJson.Options, ct)
                        ?? throw new IntakeFailureException(FailureKind.DependencyFailed, "the extraction service returned an empty answer");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
                {
                    throw new IntakeFailureException(FailureKind.DependencyFailed, "the extraction service's answer was cut off or malformed", inner: ex);
                }
            }
            throw await ReadFailureAsync(response, ct);
        }
    }

    /// <summary>
    /// Whether the extraction service has a model provider key. Asked once per request before any work is done (so a
    /// deployment without a key answers 503 at once), and remembered for a few seconds.
    /// </summary>
    /// <exception cref="IntakeFailureException">The service cannot be reached.</exception>
    public async Task<bool> IsConfiguredAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ServiceUrl))
            return false;
        if (_status is { } known && DateTimeOffset.UtcNow - known.At < StatusLifetime)
            return known.Configured;
        try
        {
            var status = await httpClientFactory.CreateClient(HttpClientName).GetFromJsonAsync<ExtractionStatus>("extraction/status", IntakeJson.Options, ct)
                ?? throw new IntakeFailureException(FailureKind.DependencyFailed, "the extraction service returned an empty status");
            _status = (DateTimeOffset.UtcNow, status.Configured);
            return status.Configured;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            throw new IntakeFailureException(FailureKind.DependencyUnavailable, "the extraction service is unreachable; retry later",
                retryAfterSeconds: 10, inner: ex);
        }
    }

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMilliseconds(750);

    /// <summary>Is the service up? Returns its readiness status, or null when unreachable.</summary>
    public async Task<string?> GetReadinessAsync(CancellationToken ct = default)
    {
        // A Service with no ready pods behind it makes connects hang rather than fail, and the probe that called us gives up after
        // a second: bound the wait so "down" is answered as Degraded instead of hanging the gateway's own readiness.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadinessTimeout);
        ct = timeout.Token;
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).GetAsync("health/ready", ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<Exception> ReadFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (await DaprErrors.ReadAsync(response, ct) is { } sidecar)
            // The sidecar's text names internal services: it goes to the log and the trace through the cause, never to the caller.
            return new IntakeFailureException(FailureKind.DependencyUnavailable, "the extraction service is unreachable; retry later", 10, inner: new HttpRequestException(sidecar));
        var problem = await ReadProblemAsync(response, ct);
        var detail = problem?.Detail ?? response.ReasonPhrase ?? "extraction service error";
        if (problem?.Extensions.TryGetValue(ServiceProblems.KindExtension, out var named) == true
            && Enum.TryParse<FailureKind>(named?.ToString(), out var kind))
        {
            var retryAfter = problem.Extensions.TryGetValue(ServiceProblems.RetryAfterExtension, out var seconds)
                && int.TryParse(seconds?.ToString(), out var parsed) ? parsed : (int?)null;
            return new IntakeFailureException(kind, detail, retryAfter) { Calls = ReadCalls(problem) };
        }
        // Without a kind it is the service's own answer (its concurrency limit, or no key), not the provider's.
        return response.StatusCode == HttpStatusCode.ServiceUnavailable
            ? new IntakeFailureException(FailureKind.DependencyUnavailable, detail, (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 10)
            : new IntakeFailureException(FailureKind.DependencyFailed, $"the extraction service returned {(int)response.StatusCode}");
    }

    private static IReadOnlyList<ModelCall> ReadCalls(ProblemDetails problem)
    {
        if (!problem.Extensions.TryGetValue(ServiceProblems.CallsExtension, out var value) || value is not JsonElement json)
            return [];
        try
        {
            return json.Deserialize<List<ModelCall>>(IntakeJson.Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task<ProblemDetails?> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemDetails>(IntakeJson.Options, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or HttpRequestException)
        {
            return null;
        }
    }
}

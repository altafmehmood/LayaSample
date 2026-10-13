using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocIntake.Contracts;
using DocIntake.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocIntake.Api.Services.Decisions;

/// <summary>
/// The gateway's way to the decisions service, where Laya, the feedback model, runs. What the service answers comes back as
/// the failure it was there (its own "model is still loading" is <see cref="FailureKind.DependencyUnavailable"/> with the
/// same text), and a service that cannot be reached is the same kind with a retry hint.
/// </summary>
public sealed class DecisionsServiceClient(IHttpClientFactory httpClientFactory, IOptions<DecisionsGatewayOptions> options)
{
    /// <summary>Name of the <see cref="HttpClient"/> configured with the decisions service's base address.</summary>
    public const string HttpClientName = "decisions-service";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ServiceUrl);

    /// <exception cref="IntakeFailureException">The service refused, was not ready, or could not be reached.</exception>
    public async Task<AnalyzeResponse> AnalyzeAsync(string text, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("decisions/feedback", new AnalyzeRequest(text), IntakeJson.Options, ct);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            throw new IntakeFailureException(FailureKind.DependencyUnavailable, "the decisions service is unreachable; retry later",
                retryAfterSeconds: 10, inner: ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new IntakeFailureException(FailureKind.DependencyFailed, "the decisions service failed", inner: ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return await response.Content.ReadFromJsonAsync<AnalyzeResponse>(IntakeJson.Options, ct)
                        ?? throw new IntakeFailureException(FailureKind.DependencyFailed, "the decisions service returned an empty answer");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
                {
                    throw new IntakeFailureException(FailureKind.DependencyFailed, "the decisions service's answer was cut off or malformed", inner: ex);
                }
            }
            throw await ReadFailureAsync(response, ct);
        }
    }

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMilliseconds(750);

    /// <summary>Is the service up? Returns its readiness status ("Unhealthy" while the model loads), or null when unreachable.</summary>
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
        ProblemDetails? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(IntakeJson.Options, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or HttpRequestException)
        {
        }
        var detail = problem?.Detail ?? response.ReasonPhrase ?? "decisions service error";
        if (problem?.Extensions.TryGetValue(ServiceProblems.KindExtension, out var named) == true && Enum.TryParse<FailureKind>(named?.ToString(), out var kind))
            return new IntakeFailureException(kind, detail);
        // The caller's own mistake (the gateway checks first, so this is a service that disagrees): pass the reason on as a bad document-free request.
        return response.StatusCode == HttpStatusCode.ServiceUnavailable
            ? new IntakeFailureException(FailureKind.DependencyUnavailable, detail, (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 10)
            : new IntakeFailureException(FailureKind.DependencyFailed, $"the decisions service returned {(int)response.StatusCode}");
    }
}

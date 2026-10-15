using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocIntake.Contracts;
using DocIntake.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace DocIntake.Api.Services.Documents;

/// <summary>
/// The gateway's way to the document service: classifying and preparing an upload happen there, in the one process that
/// reads untrusted bytes. The upload is streamed from where it sits (never copied into memory), once per call. Never
/// retries: a document that crashed the service would crash it again.
/// </summary>
/// <remarks>
/// What the service answers comes back as the failure it was on its side: a bad document is an
/// <see cref="IntakeFailureException"/> with the same kind; a service that cannot be reached or is saturated is a
/// <see cref="RendererUnavailableException"/>; one that died on the document is a <see cref="RenderingFailedException"/>.
/// </remarks>
public sealed class DocumentServiceClient(IHttpClientFactory httpClientFactory) : IDocumentClassifier, IDocumentPreparer
{
    /// <summary>Name of the <see cref="HttpClient"/> configured with the document service's base address.</summary>
    public const string HttpClientName = "document-service";

    public async Task<DocumentClassification> ClassifyAsync(Stream stream, CancellationToken ct = default)
    {
        var answer = await PostAsync<ClassifyResponse>("documents/classify", Form(stream), ct);
        // The findings travel next to the classification (it does not serialise them), and belong on it again here.
        return answer.Classification with { Risks = answer.Risks };
    }

    public async Task<PreparedDocument> PrepareAsync(Stream stream, DocumentClassification classification, CancellationToken ct = default)
    {
        var form = Form(stream);
        form.Add(new StringContent(JsonSerializer.Serialize(classification, IntakeJson.Options), Encoding.UTF8), "classification");
        var answer = await PostAsync<PrepareResponse>("documents/prepare", form, ct);
        // The findings travel next to the document (it does not serialise them), and belong on it again here.
        return answer.Document with { Risks = answer.Risks };
    }

    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMilliseconds(750);

    /// <summary>Is the service up and are its models loaded? Returns its readiness status, or null when unreachable.</summary>
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

    // LeaveOpenStream: disposing the request would otherwise close the upload, which the next stage reads again.
    private static MultipartFormDataContent Form(Stream stream)
    {
        stream.Position = 0;
        return new MultipartFormDataContent { { new StreamContent(new LeaveOpenStream(stream)), "file", "document" } };
    }

    private async Task<T> PostAsync<T>(string path, MultipartFormDataContent content, CancellationToken ct)
    {
        using (content)
        using (var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content })
            return await SendAsync<T>(request, ct);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            throw new RendererUnavailableException("document service is unreachable", ex);
        }
        // Connected, then lost the connection: the service most likely died on this document.
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new RenderingFailedException("document service dropped the request", ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return await response.Content.ReadFromJsonAsync<T>(IntakeJson.Options, ct)
                        ?? throw new RenderingFailedException("document service returned an empty response");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
                {
                    throw new RenderingFailedException("document service response was cut off or malformed", ex);
                }
            }

            if (await DaprErrors.ReadAsync(response, ct) is { } sidecar)
                throw new RendererUnavailableException("document service is unreachable", new HttpRequestException(sidecar));
            var problem = await ReadProblemAsync(response, ct);
            var detail = problem?.Detail ?? response.ReasonPhrase ?? "document service error";
            if (problem?.Extensions.TryGetValue(ServiceProblems.KindExtension, out var named) == true
                && Enum.TryParse<FailureKind>(named?.ToString(), out var kind))
                throw new IntakeFailureException(kind, detail);
            throw response.StatusCode switch
            {
                HttpStatusCode.RequestEntityTooLarge => (Exception)new IntakeFailureException(FailureKind.OverLimit, detail),
                HttpStatusCode.ServiceUnavailable when problem?.Type == OcrUnavailableException.ProblemType => new OcrUnavailableException(detail),
                HttpStatusCode.ServiceUnavailable => new RendererUnavailableException(detail),
                _ => new RenderingFailedException($"document service returned {(int)response.StatusCode}: {detail}")
            };
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

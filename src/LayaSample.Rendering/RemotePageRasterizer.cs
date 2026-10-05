using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace LayaSample.Rendering;

/// <summary>
/// Calls the renderer service (<c>LayaSample.Renderer</c>), so untrusted bytes reach native parsers only in that
/// isolated process. Never retries: a document that crashed the renderer would crash it again.
/// </summary>
public sealed class RemotePageRasterizer(IHttpClientFactory httpClientFactory) : IPageRasterizer
{
    /// <summary>Name of the <see cref="HttpClient"/> configured with the renderer's base address.</summary>
    public const string HttpClientName = "renderer";

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public async Task<IReadOnlyList<RasterPage>> InspectImageAsync(byte[] image, string mediaType, int maxFrames, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"inspect?mediaType={Uri.EscapeDataString(mediaType)}&maxFrames={maxFrames}")
        {
            Content = new ByteArrayContent(image) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }
        };
        return await SendAsync<List<RasterPage>>(request, ct);
    }

    public async Task<IReadOnlyList<RenderedPage>> RenderAsync(RenderJob job, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "render")
        {
            Content = new MultipartFormDataContent
            {
                { new ByteArrayContent(job.Document), "document", "document" },
                { JsonContent.Create(new RenderRequest(job.MediaType, job.Pages), options: Json), "job" }
            }
        };
        var response = await SendAsync<RenderResponse>(request, ct);
        return response.Pages;
    }

    /// <summary>Is the renderer up and are its models loaded? Returns its readiness status, or null when unreachable.</summary>
    public async Task<string?> GetReadinessAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).GetAsync("health/ready", ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return null;
        }
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
            throw new RendererUnavailableException("renderer is unreachable", ex);
        }
        // Connected, then lost the connection: the renderer most likely died on this document.
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new RenderingFailedException("renderer dropped the request", ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                        ?? throw new RenderingFailedException("renderer returned an empty response");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
                {
                    throw new RenderingFailedException("renderer response was cut off or malformed", ex);
                }
            }

            var problem = await ReadProblemAsync(response, ct);
            var detail = problem?.Detail ?? response.ReasonPhrase ?? "renderer error";
            throw response.StatusCode switch
            {
                HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge => (Exception)new InvalidDataException(detail),
                HttpStatusCode.ServiceUnavailable when problem?.Type == OcrUnavailableException.ProblemType => new OcrUnavailableException(detail),
                HttpStatusCode.ServiceUnavailable => new RendererUnavailableException(detail),
                _ => new RenderingFailedException($"renderer returned {(int)response.StatusCode}: {detail}")
            };
        }
    }

    private static async Task<ProblemDetails?> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemDetails>(Json, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or HttpRequestException)
        {
            return null;
        }
    }
}

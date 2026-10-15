using System.Text.Json;

namespace DocIntake.Api.Services;

/// <summary>
/// Calls to the document, extraction and scan services go through the gateway's own Dapr sidecar in Kubernetes. When that
/// sidecar cannot complete a call (the callee's pod is gone, its sidecar is not ready, or its access policy refuses the
/// gateway) it answers itself, with a JSON body <c>{"errorCode":"ERR_...","message":"..."}</c>, instead of the service's
/// answer. That is the service being unreachable, not the service failing on a document, so it must be told apart.
/// </summary>
internal static class DaprErrors
{
    /// <summary>
    /// The sidecar's own error message when <paramref name="response"/> is one, else null. Buffers the body so the caller can
    /// still read it as the service's problem document.
    /// </summary>
    public static async Task<string?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json")
            return null;
        try
        {
            await response.Content.LoadIntoBufferAsync(ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("errorCode", out var code) && code.GetString() is { } c && c.StartsWith("ERR_", StringComparison.Ordinal))
                return json.RootElement.TryGetProperty("message", out var message) ? $"{c}: {message.GetString()}" : c;
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException)
        {
            // Not a sidecar error; the caller reads the body as it always did.
        }
        return null;
    }
}

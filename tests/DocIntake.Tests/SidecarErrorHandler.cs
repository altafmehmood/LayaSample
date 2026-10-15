using System.Net;
using System.Text;

namespace DocIntake.Tests;

/// <summary>
/// What the gateway's own Dapr sidecar answers when it cannot complete a call: its own status and a JSON error body, not the
/// service's answer.
/// </summary>
internal sealed class SidecarErrorHandler(HttpStatusCode status = HttpStatusCode.InternalServerError, string code = "ERR_DIRECT_INVOKE",
    string message = "fail to invoke, id: docintake-document, err: couldn't find service") : HttpMessageHandler
{
    /// <summary>For a handler that wraps this one.</summary>
    public Task<HttpResponseMessage> SendForTest(HttpRequestMessage request, CancellationToken ct) => SendAsync(request, ct);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent($$"""{"errorCode":"{{code}}","message":"{{message}}"}""", Encoding.UTF8, "application/json")
        });
}

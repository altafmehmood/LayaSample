using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GarakShim;

// Lets garak (which posts a JSON prompt and reads a JSON answer) attack an API that takes a document upload.
//
//   garak  --POST {"text": "<prompt>"}-->  this shim  --multipart PDF-->  /api/documents/extract
//   garak  <--{"text": "HTTP 200 <body>"}--  this shim  <--response--
//
// See redteam/README.md. Only talks to a local API unless --allow-remote is given: every prompt is a paid model call.

var options = ShimOptions.Parse(args);
if (options.Error is not null)
{
    Console.Error.WriteLine(options.Error);
    return 2;
}

var apiKey = Environment.GetEnvironmentVariable("LAYA_API_KEY");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{options.Port}/");
listener.Start();
Console.WriteLine($"garak shim on http://127.0.0.1:{options.Port}/extract -> {options.Target} (run {options.RunId}, at most {options.MaxRequests} requests)");

var sent = 0;
while (true)
{
    var context = await listener.GetContextAsync();
    _ = Task.Run(() => Handle(context));
}

async Task Handle(HttpListenerContext context)
{
    try
    {
        if (context.Request.HttpMethod == "GET")
        {
            await Reply(context, 200, """{"status":"ok"}""");
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
        var prompt = body.RootElement.GetProperty("text").GetString() ?? "";

        if (Interlocked.Increment(ref sent) > options.MaxRequests)
        {
            await Reply(context, 200, JsonSerializer.Serialize(new { text = "[request budget exhausted: raise --max-requests]" }));
            return;
        }

        var answer = await Send(prompt);
        await Reply(context, 200, JsonSerializer.Serialize(new { text = answer }));
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        await Reply(context, 400, JsonSerializer.Serialize(new { error = "expected {\"text\": \"...\"}" }));
    }
}

// The prompt becomes the free-text remarks of a loan application, so the typed agent and its ordinary prompt run.
async Task<string> Send(string prompt)
{
    var pdf = PdfText.Build("LOAN APPLICATION",
        ["Applicant: Redwood Mechanical Supply, LLC", "Amount requested: $250,000", "Term: 60 months", "Purpose: working capital", "", "Remarks:"],
        prompt);
    using var form = new MultipartFormDataContent { { new ByteArrayContent(pdf) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } }, "file", "application.pdf" } };
    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Target, "api/documents/extract")) { Content = form };
    if (!string.IsNullOrEmpty(apiKey))
        request.Headers.Add("X-Api-Key", apiKey);
    request.Headers.UserAgent.ParseAdd($"laya-garak/{options.RunId}");
    try
    {
        using var response = await http.SendAsync(request);
        // Status and body as text, whatever they are: a 422 that refused the document is an answer garak should see, not an error.
        return $"HTTP {(int)response.StatusCode}\n{await response.Content.ReadAsStringAsync()}";
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return $"ERROR {ex.GetType().Name}: {ex.Message}";
    }
}

static async Task Reply(HttpListenerContext context, int status, string json)
{
    var bytes = Encoding.UTF8.GetBytes(json);
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json";
    context.Response.ContentLength64 = bytes.Length;
    await context.Response.OutputStream.WriteAsync(bytes);
    context.Response.Close();
}

namespace GarakShim
{
    public sealed record ShimOptions(Uri Target, int Port, int MaxRequests, string RunId, int TimeoutSeconds, string? Error)
    {
        public static ShimOptions Parse(string[] args)
        {
            var target = "http://localhost:8080";
            var port = 5099;
            var max = 5000;
            var timeout = 300;
            var runId = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var allowRemote = false;
            ShimOptions Fail(string error) => new(new Uri("http://localhost"), port, max, runId, timeout, error);

            for (var i = 0; i < args.Length; i++)
            {
                string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                try
                {
                    switch (args[i])
                    {
                        case "--target": target = Value(); break;
                        case "--port": port = int.Parse(Value()); break;
                        case "--max-requests": max = int.Parse(Value()); break;
                        case "--timeout": timeout = int.Parse(Value()); break;
                        case "--run-id": runId = Value(); break;
                        case "--allow-remote": allowRemote = true; break;
                        default: return Fail($"unknown option {args[i]}");
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException)
                {
                    return Fail(ex.Message);
                }
            }

            if (!Uri.TryCreate(target.EndsWith('/') ? target : target + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return Fail($"--target must be an http(s) URL, not '{target}'");
            if (!uri.IsLoopback && !allowRemote)
                return Fail($"{uri.Host} is not local. Every prompt is a paid model call and the attack text lands in that service's telemetry; pass --allow-remote to do it on purpose.");
            if (max < 1) return Fail("--max-requests must be at least 1");
            return new ShimOptions(uri, port, max, runId, timeout, null);
        }
    }
}

using System.ClientModel.Primitives;
using System.Diagnostics.Metrics;
using System.Net;
using DocIntake.ExtractionService;

namespace DocIntake.Tests;

/// <summary>
/// The retry policy through the SDK's real pipeline. The fake chat client the other tests use bypasses it, which is how a
/// policy that overflowed the stack on the first retry reached production.
/// </summary>
public class RetryPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Meters : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);
        public void Dispose() { }
    }

    private sealed class Provider(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(++Requests));

        // The SDK's synchronous pipeline calls this one.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) => respond(++Requests);
    }

    private static HttpResponseMessage Throttled()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "0");
        return response;
    }

    /// <summary>Sends one request through a pipeline that uses the policy, returning the final status and the retries counted.</summary>
    private static async Task<(int Status, long Retries, int Requests)> Send(Provider provider, int maxRetries, bool sync = false)
    {
        long retries = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ExtractionMetrics.MeterName && instrument.Name == "intake.llm.retries")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => retries += value);
        listener.Start();

        var policy = new RecordingRetryPolicy(maxRetries, new ExtractionMetrics(new Meters()));
        var pipeline = ClientPipeline.Create(new ClientPipelineOptions
        {
            RetryPolicy = policy,
            Transport = new HttpClientPipelineTransport(new HttpClient(provider))
        });
        var message = pipeline.CreateMessage();
        message.Request.Method = "GET";
        message.Request.Uri = new Uri("http://provider.test/v1/chat/completions");
        if (sync) pipeline.Send(message); else await pipeline.SendAsync(message);

        return (message.Response!.Status, retries, provider.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_rate_limited_call_is_retried_and_the_retry_is_counted_once(bool sync)
    {
        var provider = new Provider(n => n == 1 ? Throttled() : new HttpResponseMessage(HttpStatusCode.OK));

        var (status, retries, requests) = await Send(provider, maxRetries: 2, sync);

        Assert.Equal((200, 1L, 2), (status, retries, requests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_provider_that_keeps_refusing_is_given_up_on_after_the_configured_retries(bool sync)
    {
        var provider = new Provider(_ => Throttled());

        var (status, retries, requests) = await Send(provider, maxRetries: 2, sync);

        Assert.Equal((429, 2L, 3), (status, retries, requests));
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        var provider = new Provider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        var (status, retries, requests) = await Send(provider, maxRetries: 2);

        Assert.Equal((400, 0L, 1), (status, retries, requests));
    }
}

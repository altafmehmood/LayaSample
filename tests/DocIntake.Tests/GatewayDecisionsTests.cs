using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocIntake.Api.Services.Decisions;
using DocIntake.Contracts;
using DocIntake.DecisionsService;
using DocIntake.ServiceDefaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocIntake.Tests;

/// <summary>The gateway with a decisions service behind it, as deployed: the gateway holds no model.</summary>
public class GatewayDecisionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ThrowingHandler(HttpRequestError error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException(error, $"simulated {error}");
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return base.SendAsync(request, ct);
        }
    }

    /// <summary>The gateway, its other services in memory, and its decisions calls going to <paramref name="decisions"/>.</summary>
    private static WebApplicationFactory<Program> Gateway(Func<HttpMessageHandler>? decisions, string? url = "http://decisions") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // After appsettings.Development.json, which names a default address, so it wins.
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Decisions:ServiceUrl"] = url ?? "" }));
            b.ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
                s.UseDocumentService();
                if (decisions is not null)
                    s.AddHttpClient(DecisionsServiceClient.HttpClientName)
                        .ConfigureHttpClient(c => c.BaseAddress ??= new Uri("http://decisions/"))
                        .ConfigurePrimaryHttpMessageHandler(decisions);
            });
        });

    private static async Task<HttpResponseMessage> Analyze(HttpClient client, string? text) =>
        await client.PostAsJsonAsync("/api/feedback/analyze", new AnalyzeRequest(text), Ct);

    [Fact]
    public async Task A_ready_model_answers_through_the_gateway_unchanged()
    {
        using var service = new DecisionsServiceFactory(new FixedModelStatus(WarmupState.Ready), new FakeAnalyzer());
        using var api = Gateway(() => service.Server.CreateHandler());

        var response = await Analyze(api.CreateClient(), "The room was dirty");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((true, 0.9123, 4, "Severe", "Cleanliness", 0.8246), (body.GetProperty("dissatisfied").GetBoolean(),
            body.GetProperty("dissatisfactionScore").GetDouble(), body.GetProperty("rating").GetInt32(), body.GetProperty("level").GetString(),
            body.GetProperty("primaryDriver").GetString(), body.GetProperty("confidence").GetDouble()));
    }

    [Theory]
    [InlineData(WarmupState.Loading, "model is still loading")]
    [InlineData(WarmupState.Failed, "model failed to load")]
    public async Task A_model_that_cannot_answer_is_a_503_with_the_same_text_and_no_retry_hint(WarmupState state, string detail)
    {
        using var service = new DecisionsServiceFactory(new FixedModelStatus(state), new FakeAnalyzer());
        using var api = Gateway(() => service.Server.CreateHandler());

        var response = await Analyze(api.CreateClient(), "The room was dirty");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(detail, await response.Content.ReadAsStringAsync(Ct));
        Assert.Null(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Bad_text_is_refused_at_the_gateway_without_calling_the_service()
    {
        using var service = new DecisionsServiceFactory(new FixedModelStatus(WarmupState.Ready), new FakeAnalyzer());
        var calls = new CountingHandler(service.Server.CreateHandler());
        using var api = Gateway(() => calls);
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await Analyze(client, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Analyze(client, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Analyze(client, new string('a', FeedbackLimits.MaxTextLength + 1))).StatusCode);

        Assert.Equal(0, calls.Requests);
    }

    [Fact]
    public async Task An_unreachable_service_is_a_503_with_a_retry_hint_and_the_rest_of_the_gateway_works()
    {
        using var api = Gateway(() => new ThrowingHandler(HttpRequestError.ConnectionError));
        var client = api.CreateClient();

        var response = await Analyze(client, "The room was dirty");
        var ready = await client.GetAsync("/health/ready", Ct);
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Degraded", body.RootElement.GetProperty("checks").GetProperty("decisions-service").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_service_dying_on_the_request_is_a_bad_gateway()
    {
        using var api = Gateway(() => new ThrowingHandler(HttpRequestError.ResponseEnded));

        var response = await Analyze(api.CreateClient(), "The room was dirty");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_service_url_feedback_analysis_answers_503_and_the_gateway_starts_and_reports_it()
    {
        using var api = Gateway(decisions: null, url: null);
        var client = api.CreateClient();

        var response = await Analyze(client, "The room was dirty");
        var ready = await client.GetAsync("/health/ready", Ct);
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("feedback analysis is not configured", await response.Content.ReadAsStringAsync(Ct));
        var check = body.RootElement.GetProperty("checks").GetProperty("decisions-service");
        Assert.Equal("Healthy", check.GetProperty("status").GetString());
        Assert.Contains("not configured", check.GetProperty("description").GetString());
    }

    [Fact]
    public async Task An_empty_environment_variable_for_the_url_does_not_stop_the_gateway_from_starting()
    {
        using var api = Gateway(decisions: null, url: "");

        Assert.Equal(HttpStatusCode.OK, (await api.CreateClient().GetAsync("/health/live", Ct)).StatusCode);
    }

    [Fact]
    public void A_url_that_is_not_http_refuses_to_start()
    {
        using var api = Gateway(decisions: null, url: "ftp://decisions");

        var ex = Assert.ThrowsAny<Exception>(() => api.CreateClient());
        Assert.Contains("Decisions:ServiceUrl", ex.ToString());
    }
}

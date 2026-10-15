using DocIntake.ServiceDefaults;
using System.Net;
using System.Net.Http.Json;
using DocIntake.Contracts;
using DocIntake.Api.Services;
using DocIntake.Rendering;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocIntake.Tests;

public class AnalyzeEndpointTests : IClassFixture<AnalyzeEndpointTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
                // No real ports: the document service is an in-memory one.
                s.UseDocumentService();
            });
    }

    private readonly HttpClient _client;
    public AnalyzeEndpointTests(Factory factory) => _client = factory.CreateClient();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_empty_text(string? text)
    {
        var res = await _client.PostAsJsonAsync("/api/feedback/analyze", new AnalyzeRequest(text), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Rejects_overlong_text()
    {
        var res = await _client.PostAsJsonAsync("/api/feedback/analyze", new AnalyzeRequest(new string('a', 4001)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Answers_503_because_feedback_analysis_is_not_available()
    {
        var res = await _client.PostAsJsonAsync("/api/feedback/analyze", new AnalyzeRequest("Terrible stay"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task Liveness_does_not_wait_for_models()
    {
        var res = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task The_gateway_is_ready_and_lists_the_services_it_calls()
    {
        var res = await _client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var checks = doc.RootElement.GetProperty("checks");
        Assert.False(checks.TryGetProperty("decisions-service", out _));
        Assert.False(checks.TryGetProperty("laya", out _));
        Assert.False(checks.TryGetProperty("ocr", out _));
        Assert.True(checks.TryGetProperty("document-service", out _));
        Assert.True(checks.TryGetProperty("extraction-service", out _));
    }

    [Theory]
    [InlineData(WarmupState.Ready, "Healthy")]
    [InlineData(WarmupState.Loading, "Unhealthy")]
    // A model that failed for good leaves the rest of the service usable, so the instance stays in rotation.
    [InlineData(WarmupState.Failed, "Degraded")]
    public void Model_state_maps_to_health(WarmupState state, string status) =>
        Assert.Equal(status, HealthReporting.FromState(state, "feature").Status.ToString());
}

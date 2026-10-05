using System.Net;
using System.Net.Http.Json;
using LayaSample.Api.Models;
using LayaSample.Api.Services;
using LayaSample.Rendering;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LayaSample.Tests;

public class AnalyzeEndpointTests : IClassFixture<AnalyzeEndpointTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureServices(s =>
            {
                // Don't load the real model in unit tests.
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
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
    public async Task Returns_503_while_model_loading()
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
    public async Task Readiness_reports_each_model_while_loading()
    {
        var res = await _client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var checks = doc.RootElement.GetProperty("checks");
        Assert.Equal("loading", checks.GetProperty("laya").GetProperty("description").GetString());
        Assert.Equal("loading", checks.GetProperty("ocr").GetProperty("description").GetString());
    }

    [Theory]
    [InlineData(WarmupState.Ready, "Healthy")]
    [InlineData(WarmupState.Loading, "Unhealthy")]
    // A model that failed for good leaves the rest of the service usable, so the instance stays in rotation.
    [InlineData(WarmupState.Failed, "Degraded")]
    public void Model_state_maps_to_health(WarmupState state, string status) =>
        Assert.Equal(status, HealthReporting.FromState(state, "feature").Status.ToString());
}

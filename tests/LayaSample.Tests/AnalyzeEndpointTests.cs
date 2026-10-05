using System.Net;
using System.Net.Http.Json;
using LayaSample.Api.Models;
using LayaSample.Api.Services;
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
}

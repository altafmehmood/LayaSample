using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocIntake.Contracts;
using DocIntake.DecisionsService;
using DocIntake.ServiceDefaults;

namespace DocIntake.Tests;

/// <summary>A fixed model state, so no 850 MB model is loaded.</summary>
internal sealed class FixedModelStatus(WarmupState state) : IModelStatus
{
    public WarmupState State { get; } = state;
}

/// <summary>Stands in for Laya: a canned answer, and what it was asked.</summary>
internal sealed class FakeAnalyzer(AnalyzeResponse? answer = null) : IDissatisfactionAnalyzer
{
    public static readonly AnalyzeResponse Dissatisfied = new(true, 0.9123, 4, "Severe", "Cleanliness", 0.8246);

    public List<string> Asked { get; } = [];

    public Task<AnalyzeResponse> AnalyzeAsync(string feedback, CancellationToken ct = default)
    {
        Asked.Add(feedback);
        return Task.FromResult(answer ?? Dissatisfied);
    }
}

/// <summary>The decisions service over HTTP: the model's state decides what it answers.</summary>
public class DecisionsServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpResponseMessage> Ask(DecisionsServiceFactory service, string? text) =>
        await service.CreateClient().PostAsJsonAsync("/decisions/feedback", new AnalyzeRequest(text), IntakeJson.Options, Ct);

    [Fact]
    public async Task A_ready_model_answers()
    {
        var analyzer = new FakeAnalyzer();
        using var service = new DecisionsServiceFactory(new FixedModelStatus(WarmupState.Ready), analyzer);

        var response = await Ask(service, "The room was dirty");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<AnalyzeResponse>(IntakeJson.Options, Ct);
        Assert.Equal(FakeAnalyzer.Dissatisfied, answer);
        Assert.Equal(["The room was dirty"], analyzer.Asked);
    }

    [Theory]
    [InlineData(WarmupState.Loading, "model is still loading")]
    [InlineData(WarmupState.Failed, "model failed to load")]
    public async Task A_model_that_cannot_answer_says_so_with_503_and_its_kind(WarmupState state, string detail)
    {
        var analyzer = new FakeAnalyzer();
        using var service = new DecisionsServiceFactory(new FixedModelStatus(state), analyzer);

        var response = await Ask(service, "The room was dirty");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(detail, body.RootElement.GetProperty("detail").GetString());
        Assert.Equal("DependencyUnavailable", body.RootElement.GetProperty(ServiceProblems.KindExtension).GetString());
        Assert.Empty(analyzer.Asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task The_service_checks_the_text_too(string? text)
    {
        using var service = new DecisionsServiceFactory(new FixedModelStatus(WarmupState.Ready), new FakeAnalyzer());

        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(service, text)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(service, new string('a', FeedbackLimits.MaxTextLength + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Ask(service, new string('a', FeedbackLimits.MaxTextLength))).StatusCode);
    }

    [Theory]
    [InlineData(WarmupState.Ready, HttpStatusCode.OK, "Healthy")]
    [InlineData(WarmupState.Loading, HttpStatusCode.ServiceUnavailable, "Unhealthy")]
    // A model that failed for good leaves the process usable, so it stays in rotation.
    [InlineData(WarmupState.Failed, HttpStatusCode.OK, "Degraded")]
    public async Task Readiness_follows_the_model(WarmupState state, HttpStatusCode code, string status)
    {
        using var service = new DecisionsServiceFactory(new FixedModelStatus(state));

        var response = await service.CreateClient().GetAsync("/health/ready", Ct);

        Assert.Equal(code, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(status, body.RootElement.GetProperty("status").GetString());
        Assert.True(body.RootElement.GetProperty("checks").TryGetProperty("laya", out _));
    }

    [Fact]
    public async Task Liveness_does_not_wait_for_the_model()
    {
        using var service = new DecisionsServiceFactory(new FixedModelStatus(WarmupState.Loading));

        Assert.Equal(HttpStatusCode.OK, (await service.CreateClient().GetAsync("/health/live", Ct)).StatusCode);
    }
}

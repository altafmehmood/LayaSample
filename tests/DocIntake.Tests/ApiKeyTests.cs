using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocIntake.Contracts;
using DocIntake.Api.Services;
using DocIntake.Api.Services.Documents.Extraction;
using DocIntake.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DocIntake.Tests;

public class ApiKeyTests
{
    private const string Key1 = "alpha-key-0123456789-abcdefghij";
    private const string Key2 = "bravo-key-9876543210-zyxwvutsrq";

    private sealed class Factory(string? environment, string?[] keys, FakeChatClient model) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (environment is not null)
                builder.UseEnvironment(environment);
            for (var i = 0; i < keys.Length; i++)
                builder.UseSetting($"Auth:ApiKeys:{i}", keys[i]);
            // Production defaults to scanning uploads with a ClamAV daemon, which these tests do not run. Set after
            // appsettings.json, which names the default, so it wins.
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Security:Malware:Provider"] = "none" }));
            builder.ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
                s.UseDocumentService();
                s.RemoveAll<ExtractionClients>();
                s.AddSingleton(new ExtractionClients(model, model));
            });
        }
    }

    private static FakeChatClient Model() => FakeChatClient.Returning(Extracted.GenericRoot(LendingDocumentType.Ucc1, Extracted.Ucc1()));

    private static async Task<HttpResponseMessage> Extract(HttpClient client, params string[] keyHeaders)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(DocumentFixtures.TextPdf()), "file", "doc.pdf" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents/extract") { Content = form };
        foreach (var key in keyHeaders)
            request.Headers.Add("X-Api-Key", key);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> Body(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_request_without_a_key_is_rejected_before_anything_is_processed()
    {
        var model = Model();
        using var factory = new Factory("Production", [Key1], model);

        var response = await Extract(factory.CreateClient());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("ApiKey", response.Headers.WwwAuthenticate.ToString());
        Assert.Empty(model.Calls);
    }

    [Fact]
    public async Task A_wrong_key_is_rejected_and_never_echoed()
    {
        var model = Model();
        using var factory = new Factory("Production", [Key1], model);

        var response = await Extract(factory.CreateClient(), "wrong-key-0123456789-abcdefghij");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await Body(response);
        Assert.DoesNotContain("wrong-key", body);
        Assert.DoesNotContain(Key1, body);
        Assert.Empty(model.Calls);
    }

    [Theory]
    [InlineData("alpha-key-0123456789-abcdefghi")]        // one character short
    [InlineData("alpha-key-0123456789-abcdefghijk")]      // one character long
    [InlineData("ALPHA-KEY-0123456789-ABCDEFGHIJ")]       // different case
    [InlineData(" alpha-key-0123456789-abcdefghij")]      // leading space
    public async Task Only_an_exact_match_is_accepted(string supplied)
    {
        using var factory = new Factory("Production", [Key1], Model());

        Assert.Equal(HttpStatusCode.Unauthorized, (await Extract(factory.CreateClient(), supplied)).StatusCode);
    }

    [Fact]
    public async Task The_right_key_reaches_the_model()
    {
        var model = Model();
        using var factory = new Factory("Production", [Key1], model);

        var response = await Extract(factory.CreateClient(), Key1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(model.Calls);
    }

    [Fact]
    public async Task Either_of_two_keys_works_so_a_key_can_be_rotated()
    {
        using var factory = new Factory("Production", [Key1, Key2], Model());
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await Extract(client, Key1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Extract(client, Key2)).StatusCode);
    }

    [Fact]
    public async Task Blank_configured_entries_are_ignored()
    {
        using var factory = new Factory("Production", ["", Key1, "  "], Model());
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await Extract(client, Key1)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Extract(client, "")).StatusCode);
    }

    [Fact]
    public async Task The_header_is_sent_once()
    {
        using var factory = new Factory("Production", [Key1], Model());

        Assert.Equal(HttpStatusCode.Unauthorized, (await Extract(factory.CreateClient(), Key1, Key1)).StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Without_configured_keys_outside_development_extraction_fails_closed(string environment)
    {
        var model = Model();
        using var factory = new Factory(environment, [""], model);   // an empty environment variable binds as a blank entry

        var response = await Extract(factory.CreateClient(), Key1);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("api key authentication is not configured", await Body(response));
        Assert.Empty(model.Calls);
    }

    [Fact]
    public async Task In_development_without_keys_the_endpoint_is_open()
    {
        using var factory = new Factory("Development", [], Model());

        Assert.Equal(HttpStatusCode.OK, (await Extract(factory.CreateClient())).StatusCode);
    }

    [Fact]
    public async Task In_development_with_a_key_configured_it_is_enforced()
    {
        using var factory = new Factory("Development", [Key1], Model());

        Assert.Equal(HttpStatusCode.Unauthorized, (await Extract(factory.CreateClient())).StatusCode);
    }

    [Fact]
    public async Task Other_endpoints_stay_open_while_a_key_is_configured()
    {
        using var factory = new Factory("Production", [Key1], Model());
        var client = factory.CreateClient();

        foreach (var path in new[] { "/health/live", "/scalar/v1", "/openapi/v1.json" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);

        using var form = new MultipartFormDataContent { { new ByteArrayContent(DocumentFixtures.TextPdf()), "file", "doc.pdf" } };
        var analyze = await client.PostAsync("/api/documents/analyze?dispatch=false", form, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, analyze.StatusCode);

        var feedback = await client.PostAsJsonAsync("/api/feedback/analyze", new { text = "fine" }, TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.Unauthorized, feedback.StatusCode);
    }

    [Fact]
    public async Task The_openapi_document_asks_for_the_key_on_extract_only()
    {
        using var factory = new Factory("Production", [Key1], Model());

        var spec = await factory.CreateClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(spec);
        var scheme = doc.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey");
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal("X-Api-Key", scheme.GetProperty("name").GetString());

        var paths = doc.RootElement.GetProperty("paths");
        var extract = paths.GetProperty("/api/documents/extract").GetProperty("post");
        Assert.Contains("ApiKey", extract.GetProperty("security").GetRawText());
        Assert.False(paths.GetProperty("/api/documents/analyze").GetProperty("post").TryGetProperty("security", out _));
    }

    [Theory]
    [InlineData("tiny-secret-1")]
    [InlineData("has a space in it 0123456789")]
    public void An_unusable_key_stops_the_app_without_revealing_it(string key)
    {
        using var factory = new Factory("Production", [key], Model());

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var messages = string.Join(" | ", Chain(ex).Select(e => e.Message));
        Assert.Contains("Auth:ApiKeys entry 1", messages);
        Assert.DoesNotContain(key, messages);
    }

    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            yield return ex;
    }
}

using System.ClientModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LayaSample.Api.Models;
using LayaSample.Api.Services.Documents;
using LayaSample.Api.Services.Documents.Extraction;
using LayaSample.Rendering;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LayaSample.Tests;

public class ExtractDocumentEndpointTests
{
    private sealed class Factory(ExtractionClients clients) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
                s.RemoveAll<IOcrEngine>();
                s.AddSingleton<IOcrEngine>(new FakeOcr());
                s.RemoveAll<ExtractionClients>();
                s.AddSingleton(clients);
            });
    }

    private static Factory Configured(FakeChatClient client) => new(new ExtractionClients(client, client));

    private static async Task<HttpResponseMessage> Post(HttpClient client, byte[]? file)
    {
        using var form = new MultipartFormDataContent();
        if (file is not null)
            form.Add(new ByteArrayContent(file), "file", "doc.pdf");
        return await client.PostAsync("/api/documents/extract", form, TestContext.Current.CancellationToken);
    }

    private static GenericExtraction Result(params NameValue[] other) =>
        Extracted.GenericRoot(LendingDocumentType.Ucc1, Extracted.Ucc1(), other);

    [Fact]
    public async Task Without_a_configured_provider_the_endpoint_answers_503()
    {
        using var factory = new Factory(new ExtractionClients(null, null));

        var response = await Post(factory.CreateClient(), DocumentFixtures.TextPdf());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("extraction is not configured", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extracts_typed_fields_checkboxes_and_other_fields()
    {
        var fake = FakeChatClient.Returning(Result(new NameValue("County", "Multnomah", 1)));
        using var factory = Configured(fake);

        var response = await Post(factory.CreateClient(), DocumentFixtures.TextPdf());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("doc.pdf", body.GetProperty("fileName").GetString());
        Assert.Equal("Ucc1", body.GetProperty("documentType").GetString());
        Assert.Equal("generic-lending-agent", body.GetProperty("agent").GetString());
        var extraction = body.GetProperty("extraction");
        var ucc1 = extraction.GetProperty("ucc1");
        Assert.Equal("2026-0001234", ucc1.GetProperty("fileNumber").GetProperty("value").GetString());
        Assert.Equal("SOS-412876", ucc1.GetProperty("debtorOrganizationId").GetProperty("value").GetString());
        Assert.False(ucc1.GetProperty("realEstateRecords").GetProperty("checked").GetBoolean());
        Assert.True(ucc1.GetProperty("searchReport").GetProperty("allDebtors").GetProperty("checked").GetBoolean());
        Assert.Equal("Cascade Harbor Bank, N.A.", extraction.GetProperty("lender").GetProperty("name").GetProperty("value").GetString());
        var other = extraction.GetProperty("otherFields");
        Assert.Equal("County", other[0].GetProperty("name").GetString());
        Assert.Equal("Multnomah", other[0].GetProperty("value").GetString());
        Assert.Equal(1000, body.GetProperty("usage").GetProperty("inputTokens").GetInt64());
        Assert.False(body.TryGetProperty("answers", out _));
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task Rejects_a_missing_file()
    {
        using var factory = Configured(FakeChatClient.Returning(Result()));

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(factory.CreateClient(), null)).StatusCode);
    }

    [Fact]
    public async Task Provider_rate_limiting_becomes_503_with_retry_after()
    {
        using var factory = Configured(FakeChatClient.Throwing(new ClientResultException(new FakeProviderResponse(429, "12"))));

        var response = await Post(factory.CreateClient(), DocumentFixtures.TextPdf());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("12", response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
    }

    [Fact]
    public async Task A_garbled_model_answer_is_a_bad_gateway()
    {
        using var factory = Configured(new FakeChatClient(() => new Microsoft.Extensions.AI.ChatResponse(
            new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "not json at all"))));

        var response = await Post(factory.CreateClient(), DocumentFixtures.TextPdf());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task The_openapi_document_describes_the_endpoint_and_its_file_field()
    {
        using var factory = Configured(FakeChatClient.Returning(Result()));

        var spec = await factory.CreateClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(spec);
        var post = doc.RootElement.GetProperty("paths").GetProperty("/api/documents/extract").GetProperty("post");
        Assert.Equal("ExtractDocument", post.GetProperty("operationId").GetString());
        Assert.Contains("file", post.GetProperty("requestBody").GetRawText());
        Assert.DoesNotContain("questions", post.GetProperty("requestBody").GetRawText());
    }
}

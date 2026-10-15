using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using DocIntake.Api.Services.Usage;
using DocIntake.Contracts;
using DocIntake.ExtractionService;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocIntake.Tests;

/// <summary>The gateway with an extraction service behind it, as deployed: the gateway holds no key and no model.</summary>
public class GatewayExtractionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ThrowingHandler(HttpRequestError error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException(error, $"simulated {error}");
    }

    /// <summary>Counts what goes through, and keeps request bodies so a test can read what was sent.</summary>
    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return await base.SendAsync(request, ct);
        }
    }

    /// <summary>The gateway, its document service in memory, and its extraction calls going to <paramref name="extraction"/>.</summary>
    private static WebApplicationFactory<Program> Gateway(Func<HttpMessageHandler>? extraction, Func<HttpMessageHandler>? document = null,
        string? extractionUrl = "http://extraction", params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            // After appsettings.Development.json, which names a default address, so it wins.
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Extraction:ServiceUrl"] = extractionUrl ?? "" }));
            b.ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
                s.UseDocumentService();
                if (document is not null)
                    s.AddHttpClient(DocumentServiceClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(document);
                if (extraction is not null)
                    s.AddHttpClient(ExtractionServiceClient.HttpClientName)
                        .ConfigureHttpClient(c => c.BaseAddress ??= new Uri("http://extraction/"))
                        .ConfigurePrimaryHttpMessageHandler(extraction);
            });
        });

    private static async Task<HttpResponseMessage> Extract(HttpClient client, byte[]? file = null, string fileName = "doc.pdf", string? key = null)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(file ?? DocumentFixtures.TextPdf()), "file", fileName } };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents/extract") { Content = form };
        if (key is not null) request.Headers.Add("X-Api-Key", key);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Checks(HttpClient client)
    {
        var response = await client.GetAsync("/health/ready", Ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("checks").Clone();
    }

    private static FakeChatClient Model() => FakeChatClient.Returning(Extracted.GenericRoot(LendingDocumentType.Ucc1, Extracted.Ucc1()));

    [Fact]
    public async Task An_unreachable_extraction_service_fails_extraction_but_not_analysis()
    {
        using var api = Gateway(() => new ThrowingHandler(HttpRequestError.ConnectionError));
        var client = api.CreateClient();

        var extraction = await Extract(client);
        using var form = new MultipartFormDataContent { { new ByteArrayContent(DocumentFixtures.TextPdf()), "file", "doc.pdf" } };
        var analysis = await client.PostAsync("/api/documents/analyze?dispatch=false", form, Ct);
        var checks = await Checks(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, extraction.StatusCode);
        Assert.NotNull(extraction.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, analysis.StatusCode);
        Assert.Equal("Degraded", checks.GetProperty("extraction-service").GetProperty("status").GetString());
    }

    /// <summary>Says it can extract, then its sidecar cannot complete the extract call itself.</summary>
    private sealed class SidecarFailsTheCall(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.AbsolutePath == "/extraction/status"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ExtractionStatus(true), options: IntakeJson.Options) })
                : new SidecarErrorHandler(status, message: "access control policy has denied docintake-api").SendForTest(request, ct);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_sidecar_that_cannot_reach_the_extraction_service_is_unavailable_with_retry_and_leaks_nothing(HttpStatusCode status)
    {
        using var api = Gateway(() => new SidecarFailsTheCall(status));

        var res = await Extract(api.CreateClient());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.NotNull(res.Headers.RetryAfter);
        var body = await res.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("ERR_DIRECT_INVOKE", body);
        Assert.DoesNotContain("access control", body);
    }

    /// <summary>Says it can extract, then dies on the request itself.</summary>
    private sealed class DiesOnTheRequest : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.AbsolutePath == "/extraction/status"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ExtractionStatus(true), options: IntakeJson.Options) })
                : throw new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset");
    }

    [Fact]
    public async Task A_service_dying_on_the_request_is_a_bad_gateway()
    {
        using var api = Gateway(() => new DiesOnTheRequest());

        var response = await Extract(api.CreateClient());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_service_url_extraction_answers_503_and_the_gateway_starts_and_reports_it()
    {
        using var api = Gateway(extraction: null, extractionUrl: null);
        var client = api.CreateClient();

        var response = await Extract(client);
        var checks = await Checks(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("extraction is not configured", await response.Content.ReadAsStringAsync(Ct));
        var check = checks.GetProperty("extraction-service");
        Assert.Equal("Healthy", check.GetProperty("status").GetString());
        Assert.Contains("not configured", check.GetProperty("description").GetString());
    }

    [Fact]
    public async Task A_service_without_a_provider_key_turns_the_upload_away_before_anything_is_read()
    {
        using var documentService = new DocumentServiceFactory(null);
        var documents = new RecordingHandler(documentService.Server.CreateHandler());
        using var extractionService = new ExtractionServiceFactory(new ExtractionClients(null, null));
        using var api = Gateway(() => extractionService.Server.CreateHandler(), () => documents);

        var response = await Extract(api.CreateClient());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("extraction is not configured", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(documents.Requests);
    }

    [Fact]
    public async Task An_unconfigured_extraction_answers_503_before_the_upload_is_validated()
    {
        using var extractionService = new ExtractionServiceFactory(new ExtractionClients(null, null));
        using var api = Gateway(() => extractionService.Server.CreateHandler());
        using var form = new MultipartFormDataContent { { new StringContent("not a file"), "other" } };

        // No file at all: validating first would answer 400. The service is asked first, so it is 503.
        var response = await api.CreateClient().PostAsync("/api/documents/extract", form, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("extraction is not configured", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_configured_extraction_validates_the_upload_before_any_work()
    {
        var fake = Model();
        using var extractionService = new ExtractionServiceFactory(new ExtractionClients(fake, fake));
        var extraction = new RecordingHandler(new TraceContextHandler(extractionService.Server.CreateHandler()));
        using var api = Gateway(() => extraction);
        using var form = new MultipartFormDataContent { { new StringContent("not a file"), "other" } };

        var response = await api.CreateClient().PostAsync("/api/documents/extract", form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("file is required", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task The_extraction_request_carries_neither_the_files_name_nor_the_caller()
    {
        const string key = "a-long-random-key-for-this-test-0123456789";
        const string fileName = "secret-borrower-name.pdf";
        var fake = Model();
        using var extractionService = new ExtractionServiceFactory(new ExtractionClients(fake, fake));
        var extraction = new RecordingHandler(new TraceContextHandler(extractionService.Server.CreateHandler()));
        using var api = Gateway(() => extraction, settings: ("Auth:ApiKeys:0", key));

        var response = await Extract(api.CreateClient(), fileName: fileName, key: key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(extraction.Requests, r => r.Path == "/extract").Body;
        Assert.DoesNotContain("secret-borrower-name", sent);
        Assert.DoesNotContain(key, sent);
        Assert.DoesNotContain(ApiCaller.FingerprintOf(key), sent);
        // The answer still names the file: the gateway puts it back.
        Assert.Equal(fileName, (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task The_gateway_asks_the_service_once_per_upload_and_the_service_asks_the_provider_once()
    {
        var fake = Model();
        using var extractionService = new ExtractionServiceFactory(new ExtractionClients(fake, fake));
        var extraction = new RecordingHandler(new TraceContextHandler(extractionService.Server.CreateHandler()));
        using var api = Gateway(() => extraction);

        var response = await Extract(api.CreateClient());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, extraction.Requests.Count(r => r.Path == "/extract"));
        Assert.Single(fake.Calls);
    }
}

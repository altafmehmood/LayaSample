using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocIntake.Contracts;
using DocIntake.Api.Services;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Documents.Extraction;
using DocIntake.Api.Services.Usage;
using DocIntake.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocIntake.Tests;

public class UsageTests
{
    private const string Key1 = "alpha-key-0123456789-abcdefghij";
    private const string Key2 = "bravo-key-9876543210-zyxwvutsrq";
    private const string AdminKey = "admin-key-5555555555-mnopqrstuv";

    private sealed class TempDatabase : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docintake-usage-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "usage.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class TestEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static UsageStore Store(TempDatabase db) =>
        new(Options.Create(new UsageOptions { DatabasePath = db.Path }), new TestEnvironment(db.Directory), NullLogger<UsageStore>.Instance);

    private static UsageRecord Row(string model = "gpt-6-luna", string key = "aaaaaaaaaaaa", string file = "a.pdf", DateTimeOffset? at = null,
        long? input = 1000, long? output = 200, decimal? cost = 0.0002m, string outcome = "ok", int attempt = 1) =>
        new(at ?? new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero), "trace-1", key, file, 1234, "TextPdf", 2, 0, "generic-lending-agent",
            model, attempt, input, input is null ? null : 0, null, output, cost, cost is null ? null : $"{model}@2026-10-11", outcome, 1500);

    // --- pricing ---

    private static ModelPricing SolPricing() => new(Options.Create(new PricingOptions
    {
        AsOf = "2026-10-11",
        Models = new(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-6.1-sol"] = new()
            {
                Tiers =
                [
                    new PriceTier { MaxInputTokens = 272_000, InputPerMillion = 2.00m, CachedInputPerMillion = 0.10m, OutputPerMillion = 10.00m },
                    new PriceTier { InputPerMillion = 4.00m, CachedInputPerMillion = 0.20m, OutputPerMillion = 15.00m }
                ]
            }
        }
    }));

    [Fact]
    public void Cost_bills_cached_input_at_its_own_rate()
    {
        var priced = SolPricing().Price("gpt-6.1-sol", 10_000, 2_000, 1_000);

        // 8,000 x $2 + 2,000 x $0.10 + 1,000 x $10, per million tokens.
        Assert.Equal(0.0262m, priced!.CostUsd);
        Assert.Equal("gpt-6.1-sol@2026-10-11", priced.PriceKey);
    }

    [Fact]
    public void The_higher_tier_starts_above_the_input_limit()
    {
        var pricing = SolPricing();

        Assert.Equal(0.544m, pricing.Price("gpt-6.1-sol", 272_000, 0, 0)!.CostUsd);
        Assert.Equal(1.088004m, pricing.Price("gpt-6.1-sol", 272_001, 0, 0)!.CostUsd);
        Assert.Equal(1.0m, pricing.Price("GPT-6.1-SOL", 0, 0, 100_000)!.CostUsd);
    }

    [Fact]
    public void An_unknown_model_has_no_price()
    {
        Assert.Null(SolPricing().Price("gpt-unknown", 1000, 0, 100));
    }

    [Fact]
    public void The_shipped_prices_cover_both_configured_models()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DocIntake.slnx")))
            root = root.Parent;
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root!.FullName, "src", "DocIntake.Api", "appsettings.json"), optional: false).Build();
        var pricing = new PricingOptions();
        config.GetSection(PricingOptions.Section).Bind(pricing);
        var extraction = new ExtractionOptions();
        config.GetSection(ExtractionOptions.Section).Bind(extraction);

        var model = new ModelPricing(Options.Create(pricing));

        Assert.NotNull(model.Price(extraction.VisionModel, 1000, 0, 100));
        Assert.NotNull(model.Price(extraction.TextModel, 1000, 0, 100));
    }

    // --- names and CSV ---

    [Theory]
    [InlineData(@"C:\Users\me\loan.pdf", "loan.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("a\r\nb\u0007.pdf", "ab.pdf")]
    [InlineData("   ", "unnamed")]
    [InlineData(null, "unnamed")]
    [InlineData("dir/", "unnamed")]
    public void File_names_are_stripped_of_paths_and_control_characters(string? name, string expected) =>
        Assert.Equal(expected, UsageFileName.Clean(name));

    [Fact]
    public void Long_file_names_are_capped()
    {
        Assert.Equal(UsageFileName.MaxLength, UsageFileName.Clean(new string('x', 1000) + ".pdf").Length);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("plain.pdf", "plain.pdf")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("=a,b", "\"'=a,b\"")]
    public void CSV_text_cells_are_quoted_and_cannot_start_a_formula(string value, string expected) =>
        Assert.Equal(expected, UsageCsv.Text(value));

    // --- store ---

    [Fact]
    public async Task Rows_round_trip_with_an_exact_cost()
    {
        using var db = new TempDatabase();
        var store = Store(db);
        var ct = TestContext.Current.CancellationToken;

        await store.InsertAsync([Row(cost: 0.123456m), Row(input: null, output: null, cost: null, outcome: "http_503")], ct);
        var rows = await store.QueryAsync(new UsageQuery(null, null, null, null, null, null, 10), ct);

        Assert.Equal(2, rows.Count);
        Assert.Equal("http_503", rows[0].Outcome);
        Assert.Null(rows[0].InputTokens);
        Assert.Null(rows[0].CostUsd);
        Assert.Equal(0.123456m, rows[1].CostUsd);
        Assert.Equal(("a.pdf", "TextPdf", 1234L, 2), (rows[1].FileName, rows[1].DocumentKind, rows[1].FileBytes, rows[1].Pages));
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero), rows[1].Timestamp);
    }

    [Fact]
    public async Task Queries_filter_by_key_model_file_and_time_and_page_newest_first()
    {
        using var db = new TempDatabase();
        var store = Store(db);
        var ct = TestContext.Current.CancellationToken;
        var noon = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        await store.InsertAsync(
        [
            Row(key: "aaaaaaaaaaaa", file: "Loan_100%.pdf", at: noon),
            Row(key: "bbbbbbbbbbbb", file: "ucc.pdf", at: noon.AddHours(1), model: "gpt-6.1-sol"),
            Row(key: "aaaaaaaaaaaa", file: "other.pdf", at: noon.AddDays(1)),
            Row(key: "aaaaaaaaaaaa", file: "late.pdf", at: noon.AddDays(2))
        ], ct);
        var all = new UsageQuery(null, null, null, null, null, null, 100);

        Assert.Equal(["late.pdf", "other.pdf", "ucc.pdf", "Loan_100%.pdf"], (await store.QueryAsync(all, ct)).Select(r => r.FileName));
        Assert.Equal(["ucc.pdf"], (await store.QueryAsync(all with { ApiKey = "bbbbbbbbbbbb" }, ct)).Select(r => r.FileName));
        Assert.Equal(["ucc.pdf"], (await store.QueryAsync(all with { Model = "gpt-6.1-sol" }, ct)).Select(r => r.FileName));
        // A search for "100%" is plain text, not a LIKE pattern, and ignores case.
        Assert.Equal(["Loan_100%.pdf"], (await store.QueryAsync(all with { File = "LOAN_100%" }, ct)).Select(r => r.FileName));
        Assert.Equal(["ucc.pdf", "Loan_100%.pdf"], (await store.QueryAsync(all with { From = noon, To = noon.AddDays(1) }, ct)).Select(r => r.FileName));

        var page1 = await store.QueryAsync(all with { Limit = 2 }, ct);
        var page2 = await store.QueryAsync(all with { Limit = 2, Before = page1[^1].Id }, ct);
        Assert.Equal(["late.pdf", "other.pdf"], page1.Select(r => r.FileName));
        Assert.Equal(["ucc.pdf", "Loan_100%.pdf"], page2.Select(r => r.FileName));
    }

    [Fact]
    public async Task The_summary_totals_per_group_and_counts_calls_with_no_price()
    {
        using var db = new TempDatabase();
        var store = Store(db);
        var ct = TestContext.Current.CancellationToken;
        var noon = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        await store.InsertAsync(
        [
            Row(at: noon, input: 1000, output: 200, cost: 0.0002m),
            Row(at: noon.AddHours(2), input: 3000, output: 100, cost: 0.0004m),
            Row(at: noon.AddDays(1), model: "gpt-mystery", input: 500, output: 50, cost: null),
            Row(at: noon.AddDays(1), input: null, output: null, cost: null, outcome: "http_503")
        ], ct);
        var all = new UsageQuery(null, null, null, null, null, null, 0);

        var byDay = await store.SummaryAsync(all, "day", ct);
        var byModel = await store.SummaryAsync(all, "model", ct);

        Assert.Equal(["2026-10-12", "2026-10-11"], byDay.Select(g => g.Group));
        Assert.Equal((2L, 500L, 50L, 0.0m, 1L), (byDay[0].Calls, byDay[0].InputTokens, byDay[0].OutputTokens, byDay[0].CostUsd, byDay[0].Unpriced));
        Assert.Equal((2L, 4000L, 300L, 0.0006m, 0L), (byDay[1].Calls, byDay[1].InputTokens, byDay[1].OutputTokens, byDay[1].CostUsd, byDay[1].Unpriced));
        Assert.Equal(["gpt-mystery", "gpt-6-luna"], byModel.Select(g => g.Group));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SummaryAsync(all, "week", ct));
    }

    [Fact]
    public async Task The_writer_stores_queued_records_on_shutdown()
    {
        using var db = new TempDatabase();
        var store = Store(db);
        var log = new CapturingLogger<UsageWriter>();
        var writer = new UsageWriter(store, new DocumentMetrics(new NoMeters()), log);
        // The writer's own lifecycle, not the test run's: StopAsync stops waiting for the drain if its token is cancelled.
        await writer.StartAsync(CancellationToken.None);

        for (var i = 0; i < 250; i++)
            writer.Record(Row(file: $"f{i}.pdf"));
        await writer.StopAsync(CancellationToken.None);

        var rows = await store.QueryAsync(new UsageQuery(null, null, null, null, null, null, 1000), TestContext.Current.CancellationToken);
        Assert.True(rows.Count == 250, $"stored {rows.Count} of 250; writer task {writer.ExecuteTask?.Status}; log: {string.Join(" | ", log.Messages)}");
    }

    [Fact]
    public async Task Records_queued_before_the_writer_loop_first_runs_are_still_stored_on_shutdown()
    {
        // The host schedules ExecuteAsync, so stopping straight after starting often cancels it before it ever ran.
        // Repeated so that case is hit on every machine, not only on a busy CI runner.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var db = new TempDatabase();
            var store = Store(db);
            var writer = new UsageWriter(store, new DocumentMetrics(new NoMeters()), NullLogger<UsageWriter>.Instance);
            await writer.StartAsync(CancellationToken.None);

            for (var i = 0; i < 5; i++)
                writer.Record(Row(file: $"f{i}.pdf"));
            await writer.StopAsync(CancellationToken.None);

            var rows = await store.QueryAsync(new UsageQuery(null, null, null, null, null, null, 100), TestContext.Current.CancellationToken);
            Assert.True(rows.Count == 5, $"attempt {attempt}: stored {rows.Count} of 5; writer task {writer.ExecuteTask?.Status}");
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }

    [Fact]
    public async Task A_store_that_cannot_write_never_fails_the_caller()
    {
        using var db = new TempDatabase();
        Directory.CreateDirectory(db.Directory);
        // The database path is an existing directory, so opening it as a file fails.
        var path = Path.Combine(db.Directory, "blocked");
        Directory.CreateDirectory(path);
        var store = new UsageStore(Options.Create(new UsageOptions { DatabasePath = path }), new TestEnvironment(db.Directory), NullLogger<UsageStore>.Instance);
        var writer = new UsageWriter(store, new DocumentMetrics(new NoMeters()), NullLogger<UsageWriter>.Instance);
        await writer.StartAsync(TestContext.Current.CancellationToken);

        writer.Record(Row());
        await writer.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Nothing_is_queued_while_tracking_is_off()
    {
        var store = new UsageStore(Options.Create(new UsageOptions { DatabasePath = "" }), new TestEnvironment(Path.GetTempPath()), NullLogger<UsageStore>.Instance);
        var writer = new UsageWriter(store, new DocumentMetrics(new NoMeters()), NullLogger<UsageWriter>.Instance);

        writer.Record(Row());

        Assert.False(store.Enabled);
    }

    private sealed class NoMeters : System.Diagnostics.Metrics.IMeterFactory
    {
        public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) => new(options);
        public void Dispose() { }
    }

    // --- endpoints ---

    private sealed class Factory(string? databasePath, FakeChatClient model) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Auth:ApiKeys:0", Key1);
            builder.UseSetting("Auth:ApiKeys:1", Key2);
            builder.UseSetting("Auth:AdminKeys:0", AdminKey);
            // After appsettings.json (which names a default path), so it wins.
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Usage:DatabasePath"] = databasePath ?? "" }));
            builder.ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
                // The writer is the one hosted service these tests need.
                s.AddHostedService(sp => sp.GetRequiredService<UsageWriter>());
                s.UseDocumentService();
                s.RemoveAll<ExtractionClients>();
                s.AddSingleton(new ExtractionClients(model, model));
            });
        }
    }

    private static FakeChatClient Model() => FakeChatClient.Returning(Extracted.GenericRoot(LendingDocumentType.Ucc1, Extracted.Ucc1()));

    private static async Task<HttpResponseMessage> Extract(HttpClient client, string key, string fileName = "doc.pdf")
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(DocumentFixtures.TextPdf()), "file", fileName } };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents/extract") { Content = form };
        request.Headers.Add("X-Api-Key", key);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string url, string? key, string? accept = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (key is not null)
            request.Headers.Add("X-Api-Key", key);
        if (accept is not null)
            request.Headers.Add("Accept", accept);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>The writer stores in the background, so wait until the rows are there.</summary>
    private static async Task<JsonElement> UsageWhen(HttpClient client, string key, int rows, string query = "")
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var response = await Get(client, "/api/usage" + query, key);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.OK && body.GetProperty("count").GetInt32() >= rows)
                return body;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"usage never reached {rows} row(s)");
    }

    [Fact]
    public async Task An_extraction_is_recorded_and_visible_only_to_its_own_key()
    {
        using var db = new TempDatabase();
        using var factory = new Factory(db.Path, Model());
        var client = factory.CreateClient();

        var extraction = await Extract(client, Key1);
        Assert.Equal(HttpStatusCode.OK, extraction.StatusCode);
        var usage = (await extraction.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("usage");
        Assert.Equal(1000, usage.GetProperty("inputTokens").GetInt64());
        Assert.Equal(0.0002m, usage.GetProperty("costUsd").GetDecimal());

        var own = await UsageWhen(client, Key1, 1);
        var row = own.GetProperty("rows")[0];
        Assert.Equal(ApiCaller.FingerprintOf(Key1), row.GetProperty("apiKeyFingerprint").GetString());
        Assert.Equal(("doc.pdf", "gpt-6-luna", "ok", 1), (row.GetProperty("fileName").GetString(), row.GetProperty("model").GetString(),
            row.GetProperty("outcome").GetString(), row.GetProperty("attempt").GetInt32()));
        Assert.Equal((1000L, 200L, 0.0002m), (row.GetProperty("inputTokens").GetInt64(), row.GetProperty("outputTokens").GetInt64(), row.GetProperty("costUsd").GetDecimal()));
        Assert.DoesNotContain(Key1, own.GetRawText());

        var other = await Get(client, "/api/usage", Key2);
        Assert.Equal(0, (await other.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("count").GetInt32());
        var admin = await UsageWhen(client, AdminKey, 1);
        Assert.Equal(1, admin.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Usage_downloads_as_csv_and_a_formula_in_a_file_name_is_defused()
    {
        using var db = new TempDatabase();
        using var factory = new Factory(db.Path, Model());
        var client = factory.CreateClient();
        await Extract(client, Key1, fileName: "=1+1.pdf");
        await UsageWhen(client, Key1, 1);

        var byFormat = await Get(client, "/api/usage?format=csv", Key1);
        var byAccept = await Get(client, "/api/usage", Key1, accept: "text/csv");

        foreach (var response in new[] { byFormat, byAccept })
        {
            Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.StartsWith("docintake-usage-", response.Content.Headers.ContentDisposition.FileName);
        }
        var bytes = await byFormat.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("id,timestamp,trace_id,api_key_fingerprint,file_name,", lines[0]);
        Assert.Equal(2, lines.Length);
        // An apostrophe in front, so a spreadsheet shows the name instead of running it.
        Assert.Contains(",'=1+1.pdf,", lines[1]);
        Assert.Contains(",0.000200,", lines[1]);
    }

    [Fact]
    public async Task Usage_needs_a_key_and_rejects_bad_parameters()
    {
        using var db = new TempDatabase();
        using var factory = new Factory(db.Path, Model());
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(client, "/api/usage", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(client, "/api/usage/summary", "wrong-key-wrong-key-wrong-key")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, "/api/usage?format=xml", Key1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, "/api/usage?limit=0", Key1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, "/api/usage?limit=10001", Key1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, "/api/usage?from=2026-10-12&to=2026-10-11", Key1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, "/api/usage/summary?groupBy=week", Key1)).StatusCode);
    }

    [Fact]
    public async Task The_summary_totals_calls_tokens_and_cost()
    {
        using var db = new TempDatabase();
        using var factory = new Factory(db.Path, Model());
        var client = factory.CreateClient();
        await Extract(client, Key1);
        await Extract(client, Key1, "second.pdf");
        await UsageWhen(client, Key1, 2);

        var response = await Get(client, "/api/usage/summary?groupBy=model", Key1);
        var summary = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal("model", summary.GetProperty("groupBy").GetString());
        var group = Assert.Single(summary.GetProperty("groups").EnumerateArray());
        Assert.Equal(("gpt-6-luna", 2, 2000L, 400L), (group.GetProperty("group").GetString(), group.GetProperty("calls").GetInt32(),
            group.GetProperty("inputTokens").GetInt64(), group.GetProperty("outputTokens").GetInt64()));
        Assert.Equal(0.0004m, summary.GetProperty("total").GetProperty("costUsd").GetDecimal());

        var csv = await (await Get(client, "/api/usage/summary?format=csv", Key1)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("group,calls,input_tokens", csv);
        Assert.Contains("total,2,2000,0,0,400,0.000400,0", csv);
    }

    [Fact]
    public async Task Paging_follows_the_next_id()
    {
        using var db = new TempDatabase();
        using var factory = new Factory(db.Path, Model());
        var client = factory.CreateClient();
        var mine = ApiCaller.FingerprintOf(Key1);
        await factory.Services.GetRequiredService<UsageStore>().InsertAsync(
            Enumerable.Range(0, 5).Select(i => Row(key: mine, file: $"f{i}.pdf")).ToList(), TestContext.Current.CancellationToken);

        var page1 = await UsageWhen(client, Key1, 2, "?limit=2");
        var page2 = await UsageWhen(client, Key1, 2, $"?limit=2&before={page1.GetProperty("nextBefore").GetInt64()}");
        var page3 = await UsageWhen(client, Key1, 1, $"?limit=2&before={page2.GetProperty("nextBefore").GetInt64()}");

        Assert.Equal(["f4.pdf", "f3.pdf"], page1.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("fileName").GetString()));
        Assert.Equal(["f2.pdf", "f1.pdf"], page2.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("fileName").GetString()));
        Assert.Equal(["f0.pdf"], page3.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("fileName").GetString()));
        Assert.Equal(JsonValueKind.Null, page3.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Usage_answers_503_when_no_database_is_configured()
    {
        using var factory = new Factory(null, Model());
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Get(client, "/api/usage", Key1)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Get(client, "/api/usage/summary", Key1)).StatusCode);
        // Extraction itself still works.
        Assert.Equal(HttpStatusCode.OK, (await Extract(client, Key1)).StatusCode);
    }
}

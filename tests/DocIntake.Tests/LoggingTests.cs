using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using DocIntake.Rendering;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocIntake.Tests;

/// <summary>What the logs say: stable EventIds, the request's scope, no file names, and no stray unstructured log calls.</summary>
public partial class LoggingTests
{
    private sealed record Line(LogLevel Level, EventId Id, string Message, Dictionary<string, object?> Scope);

    private sealed class Capture : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
        public ConcurrentQueue<Line> Lines { get; } = new();
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void Dispose() { }

        private sealed class CaptureLogger(Capture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format)
            {
                var scope = new Dictionary<string, object?>();
                owner._scopes.ForEachScope((s, d) =>
                {
                    if (s is IEnumerable<KeyValuePair<string, object>> pairs)
                        foreach (var (k, v) in pairs) d[k] = v;
                }, scope);
                owner.Lines.Enqueue(new Line(level, id, format(state, exception), scope));
            }
        }
    }

    private sealed class Factory(Capture capture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.ConfigureServices(s =>
            {
                foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(d);
                s.UseDocumentService();
                s.AddLogging(b => b.AddProvider(capture));
            });
    }

    private static async Task<HttpStatusCode> Post(Factory factory, byte[] file, string name)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents/analyze")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(file), "file", name } }
        };
        return (await factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
    }

    [Fact]
    public async Task A_request_is_logged_with_its_event_id_and_a_scope_that_says_who_and_what()
    {
        var capture = new Capture();
        using var factory = new Factory(capture);

        var status = await Post(factory, DocumentFixtures.TextPdf(), "borrower-jane-doe-ssn.pdf");

        Assert.Equal(HttpStatusCode.OK, status);
        var line = Assert.Single(capture.Lines, l => l.Id.Id == 1002);
        Assert.Equal("DocumentAnalysed", line.Id.Name);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("AnalyzeDocument", line.Scope["intake.endpoint"]);
        Assert.Equal("anonymous", line.Scope["intake.caller.fingerprint"]);
        Assert.Equal(false, line.Scope["intake.caller.admin"]);
        // A file name can be personal data: it never reaches a log line, whatever the level.
        Assert.DoesNotContain(capture.Lines, l => l.Message.Contains("jane", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_refused_file_is_a_failure_with_its_own_event_id_at_information()
    {
        var capture = new Capture();
        using var factory = new Factory(capture);

        var status = await Post(factory, [1, 2, 3, 4, 5, 6, 7, 8], "notes.txt");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, status);
        var line = Assert.Single(capture.Lines, l => l.Id.Id == 1011);
        Assert.Equal("DocumentFailed", line.Id.Name);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.DoesNotContain(capture.Lines, l => l.Level >= LogLevel.Error);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocIntake.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [GeneratedRegex(@"EventId\s*=\s*(\d+)")]
    private static partial Regex EventIdPattern();

    // Each service owns a range, so an id in a log line says where it came from.
    [Theory]
    [InlineData("src/DocIntake.Api/Log.cs", 1000, 1999, 5000, 6999)]
    [InlineData("src/DocIntake.DocumentService/Log.cs", 2000, 2999, 2000, 2999)]
    [InlineData("src/DocIntake.ExtractionService/Log.cs", 3000, 3999, 3000, 3999)]
    [InlineData("src/DocIntake.DecisionsService/Log.cs", 7100, 7199, 7100, 7199)]
    [InlineData("src/DocIntake.Rendering/Log.cs", 7200, 7299, 7200, 7299)]
    public void Event_ids_stay_in_the_range_of_the_project_that_writes_them(string path, int lo, int hi, int lo2, int hi2)
    {
        var ids = EventIdPattern().Matches(File.ReadAllText(Path.Combine(Root(), path))).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.True((id >= lo && id <= hi) || (id >= lo2 && id <= hi2), $"{id} is outside the range of {path}"));
    }

    [Fact]
    public void Event_ids_are_unique_across_the_system()
    {
        var all = Directory.GetFiles(Path.Combine(Root(), "src"), "Log.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => EventIdPattern().Matches(File.ReadAllText(f)).Select(m => int.Parse(m.Groups[1].Value))).ToList();
        Assert.True(all.Count >= 30);
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [GeneratedRegex(@"\.(LogTrace|LogDebug|LogInformation|LogWarning|LogError|LogCritical)\(")]
    private static partial Regex UnstructuredLogCall();

    // Log lines are defined once, in a Log.cs, with an EventId; a call site cannot invent a message.
    [Fact]
    public void No_service_writes_a_log_line_outside_its_log_class()
    {
        var offenders = Directory.GetFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && Path.GetFileName(f) != "Log.cs")
            .Where(f => UnstructuredLogCall().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(Root(), f)).ToList();
        Assert.Empty(offenders);
    }
}

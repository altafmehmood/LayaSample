using System.Diagnostics;
using DocIntake.Api.Services;
using DocIntake.Api.Services.Documents;
using DocIntake.Api.Services.Security;
using DocIntake.Contracts;
using DocIntake.Rendering;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DocIntake.Tests;

/// <summary>The one place a failure becomes an HTTP answer: status, Retry-After, the outcome on the span, and what is logged.</summary>
public class ProblemMappingTests
{
    private sealed class NoMeterFactory : System.Diagnostics.Metrics.IMeterFactory
    {
        public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) => new(options);
        public void Dispose() { }
    }

    private sealed class ActivityFeature(Activity activity) : IHttpActivityFeature
    {
        public Activity Activity { get; set; } = activity;
    }

    private sealed record Entry(LogLevel Level, Exception? Exception, string Message);

    private sealed class CapturingLogger : ILogger<ProblemMapping>
    {
        public List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format) =>
            Entries.Add(new Entry(level, exception, format(state, exception)));
    }

    private sealed class Call(ProblemMapping mapping, CapturingLogger log, Activity span, DefaultHttpContext context)
    {
        public CapturingLogger Log { get; } = log;

        public ProblemHttpResult Map(Exception ex) =>
            Assert.IsType<ProblemHttpResult>(mapping.ToResult(ex, 1234, null, context));

        public ProblemHttpResult MapService(Exception ex) =>
            Assert.IsType<ProblemHttpResult>(mapping.ToServiceResult(ex, context));

        public DefaultHttpContext Context => context;

        public string? Header => context.Response.Headers.RetryAfter.ToString() is { Length: > 0 } h ? h : null;
        public object? Tag(string key) => span.GetTagItem(key);

        public ValueTask<bool> Handle(Exception ex) => mapping.TryHandleAsync(context, ex, CancellationToken.None);
    }

    private static Call Start()
    {
        var log = new CapturingLogger();
        var span = new Activity("test").Start();
        // Executing the answer (the handler does) needs logging and JSON options from the request's services.
        var context = new DefaultHttpContext { RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Features.Set<IHttpActivityFeature>(new ActivityFeature(span));
        return new Call(new ProblemMapping(log, new DocumentMetrics(new NoMeterFactory())), log, span, context);
    }

    // Every status the endpoints have ever answered for these failures. The test below fails when a kind is added without a row.
    private static readonly Dictionary<FailureKind, int> Expected = new()
    {
        [FailureKind.Unsupported] = 415,
        [FailureKind.Unreadable] = 422,
        [FailureKind.OverLimit] = 422,
        [FailureKind.Malicious] = 422,
        [FailureKind.PolicyRejected] = 422,
        [FailureKind.Declined] = 422,
        [FailureKind.Busy] = 503,
        [FailureKind.Misconfigured] = 503,
        [FailureKind.Timeout] = 504,
        [FailureKind.DependencyFailed] = 502,
        [FailureKind.DependencyUnavailable] = 503
    };

    public static TheoryData<FailureKind, int> Statuses
    {
        get
        {
            var data = new TheoryData<FailureKind, int>();
            foreach (var (kind, status) in Expected) data.Add(kind, status);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public void Each_kind_has_its_status(FailureKind kind, int status) => Assert.Equal(status, ProblemMapping.StatusOf(kind));

    [Fact]
    public void Every_kind_is_in_the_table() =>
        Assert.Equal(Enum.GetValues<FailureKind>().Order(), Expected.Keys.Order());

    [Fact]
    public void The_answer_carries_the_status_and_the_message_and_the_retry_hint()
    {
        var call = Start();

        var result = call.Map(new IntakeFailureException(FailureKind.Busy, "extraction is busy; retry later", retryAfterSeconds: 12));

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("extraction is busy; retry later", result.ProblemDetails.Detail);
        Assert.Equal("12", call.Header);
    }

    [Theory]
    [InlineData(FailureKind.Unsupported, "rejected")]
    [InlineData(FailureKind.Unreadable, "rejected")]
    [InlineData(FailureKind.OverLimit, "rejected")]
    [InlineData(FailureKind.Malicious, "rejected")]
    [InlineData(FailureKind.PolicyRejected, "rejected")]
    [InlineData(FailureKind.Declined, "extraction_failed")]
    [InlineData(FailureKind.Busy, "extraction_failed")]
    [InlineData(FailureKind.Timeout, "extraction_failed")]
    [InlineData(FailureKind.DependencyFailed, "extraction_failed")]
    [InlineData(FailureKind.DependencyUnavailable, "extraction_failed")]
    [InlineData(FailureKind.Misconfigured, "provider_misconfigured")]
    public void The_request_span_says_how_it_ended(FailureKind kind, string outcome)
    {
        var call = Start();

        call.Map(new IntakeFailureException(kind, "x"));

        Assert.Equal(outcome, call.Tag("intake.outcome"));
        Assert.Equal("unclassified", call.Tag("intake.document.kind"));
    }

    [Fact]
    public void A_feedback_failure_is_not_called_an_extraction_failure()
    {
        var call = Start();
        // As the exception middleware leaves it: no endpoint on the context, the original one on the exception feature.
        call.Context.SetEndpoint(null);
        call.Context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature
        {
            Error = new InvalidOperationException(),
            Endpoint = new Endpoint(null, new EndpointMetadataCollection(new EndpointNameMetadata("AnalyzeFeedback")), "feedback")
        });

        call.MapService(new IntakeFailureException(FailureKind.DependencyUnavailable, "the decisions service is unreachable; retry later", 10));

        Assert.Equal("feedback_failed", call.Tag("intake.outcome"));
        Assert.Null(call.Tag("intake.document.kind"));
    }

    [Fact]
    public void A_failure_that_is_not_about_a_document_records_no_document_metric_and_no_kind()
    {
        var call = Start();

        var result = call.MapService(new IntakeFailureException(FailureKind.Busy, "extraction is busy; retry later", 12));

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("12", call.Header);
        Assert.Equal("extraction_failed", call.Tag("intake.outcome"));
        Assert.Null(call.Tag("intake.document.kind"));
    }

    [Fact]
    public void The_cause_is_the_type_of_the_inner_exception()
    {
        var call = Start();

        call.Map(new IntakeFailureException(FailureKind.Unreadable, "PDF could not be read", inner: new InvalidDataException("bad xref")));

        Assert.Equal(nameof(InvalidDataException), call.Tag("intake.failure.cause"));
    }

    [Fact]
    public void A_bad_file_logs_why_the_library_rejected_it()
    {
        var call = Start();
        var inner = new InvalidDataException("bad xref");

        call.Map(new IntakeFailureException(FailureKind.Unreadable, "PDF could not be read", inner: inner));

        var entry = Assert.Single(call.Log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Same(inner, entry.Exception);
    }

    [Theory]
    [InlineData(FailureKind.DependencyFailed, LogLevel.Warning)]
    [InlineData(FailureKind.DependencyUnavailable, LogLevel.Warning)]
    [InlineData(FailureKind.Declined, LogLevel.Warning)]
    [InlineData(FailureKind.Misconfigured, LogLevel.Error)]
    public void A_provider_failure_never_logs_the_provider_text(FailureKind kind, LogLevel level)
    {
        var call = Start();
        var inner = new InvalidOperationException("Incorrect API key provided: sk-secret-0123456789");

        call.Map(new IntakeFailureException(kind, "the extraction service failed", inner: inner));

        var entry = Assert.Single(call.Log.Entries);
        Assert.Equal(level, entry.Level);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("sk-secret", entry.Message);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
    }

    [Fact]
    public void Bad_credentials_say_what_to_check()
    {
        var call = Start();

        call.Map(new IntakeFailureException(FailureKind.Misconfigured, "extraction is unavailable"));

        Assert.Contains("check the API key", Assert.Single(call.Log.Entries).Message);
    }

    [Theory]
    [InlineData(typeof(OcrUnavailableException), 503, "ocr_unavailable", null, "OCR is unavailable; retry later")]
    [InlineData(typeof(MalwareScannerUnavailableException), 503, "scanner_unavailable", "10", "document scanning is unavailable; retry later")]
    [InlineData(typeof(RendererUnavailableException), 503, "renderer_unavailable", "10", "document processing is unavailable; retry later")]
    [InlineData(typeof(RenderingFailedException), 502, "render_failed", null, "document could not be processed")]
    public void The_rendering_and_scanner_failures_keep_their_answers(Type type, int status, string outcome, string? retryAfter, string detail)
    {
        var call = Start();

        var result = call.Map((Exception)Activator.CreateInstance(type, "internal detail", null)!);

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(detail, result.ProblemDetails.Detail);
        Assert.Equal(retryAfter, call.Header);
        Assert.Equal(outcome, call.Tag("intake.outcome"));
    }

    [Fact]
    public void Only_failures_the_endpoints_know_are_handled()
    {
        Assert.True(ProblemMapping.Handles(new IntakeFailureException(FailureKind.Timeout, "x")));
        Assert.True(ProblemMapping.Handles(new RenderingFailedException("x")));
        Assert.False(ProblemMapping.Handles(new InvalidOperationException()));
        Assert.False(ProblemMapping.Handles(new OperationCanceledException()));
    }

    [Fact]
    public async Task As_the_exception_handler_it_answers_what_it_knows_and_leaves_the_rest()
    {
        var call = Start();
        var known = new IntakeFailureException(FailureKind.Unsupported, "unsupported document type");

        var handled = await call.Handle(known);
        var left = await call.Handle(new InvalidOperationException("a bug"));
        var cancelled = await call.Handle(new OperationCanceledException());

        Assert.True(handled);
        Assert.False(left);
        Assert.False(cancelled);
        Assert.Equal(415, call.Context.Response.StatusCode);
    }

    [Fact]
    public async Task A_started_upload_is_answered_with_its_size_and_kind_and_any_other_request_without()
    {
        var withUpload = Start();
        withUpload.Context.Items[DocumentRequest.ItemKey] = new DocumentRequest(1234, new DocumentRun(new DocumentMetrics(new NoMeterFactory())));
        var without = Start();

        await withUpload.Handle(new IntakeFailureException(FailureKind.Unreadable, "PDF could not be read"));
        await without.Handle(new IntakeFailureException(FailureKind.Unreadable, "PDF could not be read"));

        Assert.Equal("unclassified", withUpload.Tag("intake.document.kind"));
        Assert.Null(without.Tag("intake.document.kind"));
        Assert.Contains("1234-byte", Assert.Single(withUpload.Log.Entries).Message);
        Assert.DoesNotContain("byte", Assert.Single(without.Log.Entries).Message);
    }
}

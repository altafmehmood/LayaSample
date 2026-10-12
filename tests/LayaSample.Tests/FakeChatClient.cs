using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Serialization;
using LayaSample.Api.Models;
using Microsoft.Extensions.AI;

namespace LayaSample.Tests;

/// <summary>Stands in for the model provider: records what the agents send and returns canned JSON or throws.</summary>
internal sealed class FakeChatClient(Func<ChatResponse> respond) : IChatClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public static FakeChatClient Returning(object payload, ChatFinishReason? finish = null, long inputTokens = 1000, long outputTokens = 200,
        long cachedTokens = 0, long? reasoningTokens = null) =>
        new(() => new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(payload, Json)))
        {
            FinishReason = finish ?? ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens, OutputTokenCount = outputTokens,
                CachedInputTokenCount = cachedTokens > 0 ? cachedTokens : null, ReasoningTokenCount = reasoningTokens
            }
        });

    public static FakeChatClient Throwing(Exception ex) => new(() => throw ex);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Add((messages.ToList(), options));
        return Task.FromResult(respond());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

internal static class Extracted
{
    public static ExtractedField Field(string? value, int? page = 1, string? evidence = null, string? normalized = null) => new()
    {
        Value = value,
        Normalized = normalized ?? value,
        Page = value is null ? null : page,
        Evidence = value is null ? null : evidence ?? value,
        Confidence = value is null ? Confidence.Low : Confidence.High
    };

    public static Borrower Borrower(string? name = "Redwood Mechanical Supply, LLC") => new(Field(name), Field(null), Field(null));

    public static Party Lender(string? name = "Cascade Harbor Bank, N.A.") => new(Field(name), Field(null));

    public static ExtractedField Empty() => Field(null);

    public static ExtractedFlag Flag(bool? isChecked, int page = 1) => new()
    {
        Checked = isChecked,
        Page = isChecked is null ? null : page,
        Evidence = isChecked is null ? null : "box label",
        Confidence = isChecked is null ? Confidence.Low : Confidence.High
    };

    public static Ucc1Section Ucc1(string? fileNumber = "2026-0001234", string? date = "01/20/2026") =>
        new(Field(fileNumber), Field(date, normalized: "2026-01-20"), Field("Oregon Secretary of State"), Field("Oregon"), Field("Equipment, Inventory"),
            Field("Limited liability company"), Field("Oregon"), Field("SOS-412876"), Empty(), Field("None"),
            Flag(false), new SearchReportRequest(Flag(true), Flag(false), Flag(false)), Field("Cascade / Loan CA-2026-0117"),
            Field("Dana Whitfield"), Field("dwhitfield@cascadeharbor.example"), Field("Cascade Harbor Bank, N.A."));

    public static Ucc3Section Ucc3(string? initialFileNumber = "2026-0001234") =>
        new(Field(initialFileNumber), Field("2026-0009999"), Field("03/02/2026", normalized: "2026-03-02"), Field("Oregon Secretary of State"), Field("Termination"),
            Flag(false), Flag(true), Flag(false), Empty(), Flag(false), Flag(false), Field("None"), Field("None"), Empty(), Empty(), Field("None"), Empty(),
            Field("Cascade Harbor Bank, N.A."), Flag(false), Field("Cascade / Loan CA-2026-0117 / UCC-3 Termination"),
            Field("Loan Documentation, (503) 555-0134"), Field("loans@cascadeharbor.example"), Field("Cascade Harbor Bank, N.A."));

    public static Ucc1Extraction Ucc1Root(string? fileNumber = "2026-0001234", IReadOnlyList<NameValue>? other = null) =>
        new(Borrower(), Lender(), Ucc1(fileNumber), other ?? []);

    public static GenericExtraction GenericRoot(LendingDocumentType type, Ucc1Section? ucc1 = null, IReadOnlyList<NameValue>? other = null) =>
        new(type, Borrower(), Lender(), null, ucc1, null, null, other ?? []);
}

/// <summary>A provider HTTP response, to build the SDK's ClientResultException in tests.</summary>
internal sealed class FakeProviderResponse(int status, string? retryAfter) : PipelineResponse
{
    private sealed class FakeHeaders(string? retryAfter) : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string? value)
        {
            value = name.Equals("retry-after", StringComparison.OrdinalIgnoreCase) ? retryAfter : null;
            return value is not null;
        }
        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = TryGetValue(name, out var v) ? [v!] : null;
            return values is not null;
        }
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
    }

    public override int Status => status;
    public override string ReasonPhrase => "";
    public override Stream? ContentStream { get; set; }
    public override BinaryData Content => BinaryData.Empty;
    protected override PipelineResponseHeaders HeadersCore { get; } = new FakeHeaders(retryAfter);
    public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;
    public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(BinaryData.Empty);
    public override void Dispose() { }
}

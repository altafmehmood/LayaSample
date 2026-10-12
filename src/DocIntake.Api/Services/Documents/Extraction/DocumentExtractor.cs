using DocIntake.Api.Services.Security;
using DocIntake.Api.Services.Usage;
using DocIntake.Contracts;

namespace DocIntake.Api.Services.Documents.Extraction;

public interface IDocumentExtractor
{
    /// <summary>Whether the extraction service can extract (it has a model provider key). Asked before any work is done.</summary>
    /// <exception cref="IntakeFailureException">The extraction service cannot be reached.</exception>
    Task<bool> IsConfiguredAsync(CancellationToken ct);

    /// <param name="context">Who asked and what was uploaded; with it, every model call is recorded in the usage history.</param>
    Task<ExtractDocumentResponse> ExtractAsync(string fileName, DocumentClassification classification, PreparedDocument document, CancellationToken ct, UsageContext? context = null);
}

/// <summary>
/// Extraction as the gateway does it: the extraction service reads the document and reports the model calls it made; the
/// gateway records them as usage (it alone knows who asked and what was uploaded), then applies the risk policy to what
/// the service found in the answer. The service never sees the caller or the file's name.
/// </summary>
public sealed class DocumentExtractor(ExtractionServiceClient client, IUsageRecorder recorder, RiskScanner riskScanner) : IDocumentExtractor
{
    public Task<bool> IsConfiguredAsync(CancellationToken ct) => client.IsConfiguredAsync(ct);

    public async Task<ExtractDocumentResponse> ExtractAsync(string fileName, DocumentClassification classification, PreparedDocument document,
        CancellationToken ct, UsageContext? context = null)
    {
        ExtractAnswer answer;
        try
        {
            answer = await client.ExtractAsync(new ExtractRequest(classification, document), ct);
        }
        catch (IntakeFailureException ex)
        {
            // The provider bills a call that failed, and the service says which calls it made.
            Record(context, ex.Calls);
            throw;
        }

        // Before the policy: a refused answer was still paid for.
        Record(context, answer.Calls);
        // An instruction that got through the prompt can be echoed into the answer; refuse it (or flag it) here.
        riskScanner.EnforceAnswer(answer.AnswerRisks);

        return new ExtractDocumentResponse(fileName, answer.DocumentType, answer.DetectedBy, answer.Agent, answer.Model, answer.Extraction,
            answer.Usage, answer.Warnings, answer.AnswerRisks);
    }

    /// <summary>When the endpoint said who asked, queues a usage record for each model call.</summary>
    private void Record(UsageContext? context, IReadOnlyList<ModelCall> calls)
    {
        if (context is null)
            return;
        foreach (var record in RecordsFor(context, calls))
            recorder.Record(record);
    }

    /// <summary>One usage record per model call: what the call cost, joined with who asked and what was uploaded.</summary>
    internal static IEnumerable<UsageRecord> RecordsFor(UsageContext context, IReadOnlyList<ModelCall> calls) =>
        calls.Select(call => new UsageRecord(
            call.At, context.TraceId, context.Caller.Fingerprint, context.FileName, context.FileBytes, context.DocumentKind,
            context.Pages, call.Images, call.Agent, call.Model, call.Attempt,
            call.Usage?.InputTokens, call.Usage?.CachedInputTokens, call.Usage?.ReasoningTokens, call.Usage?.OutputTokens,
            call.Usage?.CostUsd, call.PriceKey, call.Outcome, call.DurationMs));
}

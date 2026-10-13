using System.Diagnostics;
using DocIntake.Contracts;
using DocIntake.ServiceDefaults;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DocIntake.ExtractionService;

/// <summary>
/// Picks the agent for a document from its text (<see cref="LendingTypeDetector"/>), sends it as page images when it has
/// them and as text otherwise, then checks the answer. One model call per document; a second only when a type agent
/// found next to nothing and the generic agent gets to look. Every model call is reported in the answer (or with the
/// failure), so whoever asked can record what was spent.
/// </summary>
public sealed class ExtractionOrchestrator(
    IOptions<ExtractionOptions> options,
    ExtractionClients clients,
    IExtractionErrorMapper errors,
    IEnumerable<IExtractionAgent> agents,
    ExtractionMetrics metrics,
    ModelPricing pricing,
    ILogger<ExtractionOrchestrator> logger)
{
    private readonly Dictionary<LendingDocumentType, IExtractionAgent> _agents = agents.ToDictionary(a => a.Type);

    public bool IsConfigured => clients.IsConfigured;

    private static readonly System.Text.Json.JsonSerializerOptions ResultJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>The user message as text, with each page image reduced to its size.</summary>
    private static string DescribeDocument(IReadOnlyList<ChatMessage> messages) =>
        string.Join("\n", messages.Where(m => m.Role == ChatRole.User).SelectMany(m => m.Contents).Select(c => c switch
        {
            TextContent t => t.Text,
            DataContent d => $"[image {d.MediaType}, {d.Data.Length} bytes]",
            _ => $"[{c.GetType().Name}]"
        }));

    /// <exception cref="IntakeFailureException">The extraction failed; its <see cref="IntakeFailureException.Calls"/> are the model calls made.</exception>
    public async Task<ExtractAnswer> ExtractAsync(ExtractRequest request, CancellationToken ct)
    {
        using var span = Telemetry.Source.StartActivity("intake.extraction.run");
        var started = Stopwatch.GetTimestamp();
        metrics.ExtractionStarted();
        var calls = new List<ModelCall>();
        var outcome = "ok";
        var agentName = "none";
        var modelName = "none";
        try
        {
            var answer = await ExtractCoreAsync(request.Classification, request.Document, span, calls, ct);
            (agentName, modelName) = (answer.Agent, answer.Model);
            return answer;
        }
        catch (Exception ex)
        {
            outcome = ex switch
            {
                IntakeFailureException e when e.Kind == FailureKind.Misconfigured => "misconfigured",
                // The usage history has always recorded the HTTP status the caller got; keep those values.
                IntakeFailureException e => "http_" + ServiceProblems.StatusOf(e.Kind),
                OperationCanceledException => "cancelled",
                _ => "error"
            };
            span.RecordFailure(ex, outcome);
            if (ex is IntakeFailureException failure)
                failure.Calls = calls;
            throw;
        }
        finally
        {
            metrics.ExtractionFinished();
            metrics.RecordExtractionRun(agentName, modelName, Stopwatch.GetElapsedTime(started), outcome);
            span?.SetTag("intake.outcome", outcome);
        }
    }

    private async Task<ExtractAnswer> ExtractCoreAsync(DocumentClassification classification, PreparedDocument document,
        Activity? span, List<ModelCall> calls, CancellationToken ct)
    {
        var opts = options.Value;
        var detected = LendingTypeDetector.Detect(document);
        var agent = _agents[detected];  // Other maps to the generic agent
        var warnings = new List<string>();
        span?.SetTag("intake.extraction.detected_type", detected.ToString());
        span?.SetTag("intake.extraction.initial_agent", agent.Name);

        var (output, model) = await RunAsync(agent, document, opts, warnings, attempt: 1, calls, ct);
        var checkWarnings = ExtractionValidator.Validate(output.Result, document, classification.PageCount);

        if (detected != LendingDocumentType.Other && ExtractionValidator.IsMostlyEmpty(output.Result))
        {
            logger.GenericRetry(agent.Name);
            span?.AddEvent(new ActivityEvent("intake.extraction.generic_retry", tags: new ActivityTagsCollection { ["intake.extraction.first_agent"] = agent.Name }));
            span?.SetTag("intake.extraction.generic_retry", true);
            var generic = _agents[LendingDocumentType.Other];
            var retryWarnings = new List<string>();
            var (retry, retryModel) = await RunAsync(generic, document, opts, retryWarnings, attempt: 2, calls, ct);
            var retryChecks = ExtractionValidator.Validate(retry.Result, document, classification.PageCount);
            warnings.Add($"the document did not look like a {detected}; the generic agent was used instead");
            output = retry with { Usage = output.Usage.Plus(retry.Usage) };
            (checkWarnings, agent, model) = (retryChecks, generic, retryModel);
            warnings.AddRange(retryWarnings);
        }
        warnings.AddRange(checkWarnings);
        output = output with { Result = output.Result with { OtherFields = ExtractionValidator.CleanOtherFields(output.Result.OtherFields, classification.PageCount) } };
        // An instruction that got through the prompt can be echoed into the answer. Reported here; the gateway decides.
        var answerRisks = AnswerRisks.Find(output.Result);
        if (answerRisks.Count > 0)
            warnings.Add("the answer repeats instruction-like text from the document; treat the values as data");

        metrics.RecordExtraction(agent.Name, output.Result.DocumentType.ToString(), model);
        var allFields = ExtractionValidator.Fields(output.Result).ToList();
        var found = allFields.Count(f => !string.IsNullOrWhiteSpace(f.Value));
        metrics.RecordFields(agent.Name, found, allFields.Count);
        metrics.RecordWarnings(agent.Name, warnings.Count);
        span?.SetTag("intake.extraction.agent", agent.Name);
        span?.SetTag("intake.extraction.model", model);
        span?.SetTag("intake.extraction.document_type", output.Result.DocumentType.ToString());
        span?.SetTag("intake.extraction.fields_found", found);
        span?.SetTag("intake.extraction.fields_total", allFields.Count);
        span?.SetTag("intake.extraction.other_fields", output.Result.OtherFields?.Count ?? 0);
        span?.SetTag("intake.extraction.warnings", warnings.Count);
        span?.SetTag("intake.extraction.input_tokens", output.Usage.InputTokens);
        span?.SetTag("intake.extraction.output_tokens", output.Usage.OutputTokens);
        span?.SetTag("intake.extraction.cached_tokens", output.Usage.CachedInputTokens);
        if (output.Usage.ReasoningTokens is { } reasoning) span?.SetTag("intake.extraction.reasoning_tokens", reasoning);
        if (output.Usage.CostUsd is { } cost) span?.SetTag("intake.extraction.cost_usd", (double)cost);
        if (warnings.Count > 0)
        {
            logger.ExtractionWarnings(output.Result.DocumentType, warnings.Count, warnings);
            foreach (var warning in warnings)
                span?.AddEvent(new ActivityEvent("intake.extraction.warning", tags: new ActivityTagsCollection { ["intake.extraction.warning.text"] = warning }));
        }
        // Counts and kinds only, never names, values or quotes: all of them can be personal data.
        logger.Extracted(output.Result.DocumentType, agent.Name, model, found, allFields.Count, output.Usage.InputTokens, output.Usage.OutputTokens);

        return new ExtractAnswer(output.Result.DocumentType, agent.Type == LendingDocumentType.Other ? "model" : "keywords",
            agent.Name, model, output.Result, output.Usage, warnings, answerRisks, calls);
    }

    private async Task<(AgentOutput Output, string Model)> RunAsync(IExtractionAgent agent, PreparedDocument document,
        ExtractionOptions opts, List<string> warnings, int attempt, List<ModelCall> calls, CancellationToken ct)
    {
        var request = ExtractionMessageBuilder.Build(agent.SystemPrompt, document, opts);
        warnings.AddRange(request.Warnings);
        var client = request.UsesImages ? clients.Vision! : clients.Text!;
        var model = request.UsesImages ? opts.VisionModel : opts.TextModel;
        var images = request.Messages.SelectMany(m => m.Contents).OfType<DataContent>().Count();
        using var span = Telemetry.Source.StartActivity("intake.extraction.attempt");
        // This is a technology demonstrator, so prompts and answers are recorded to debug extraction quality. Page
        // images are not (megabytes of base64): only their count and size. Do not carry this over to real documents.
        span?.SetTag("intake.prompt.system", Telemetry.Truncate(agent.SystemPrompt));
        span?.SetTag("intake.prompt.document", Telemetry.Truncate(DescribeDocument(request.Messages)));
        span?.SetTag("intake.prompt.images", images);
        span?.SetTag("intake.extraction.agent", agent.Name);
        span?.SetTag("intake.extraction.model", model);
        span?.SetTag("intake.extraction.uses_images", request.UsesImages);
        span?.SetTag("intake.extraction.attempt", attempt);
        var started = Stopwatch.GetTimestamp();

        void Record(ExtractionUsage? usage, PricedUsage? priced, string outcome) =>
            RecordCall(calls, agent.Name, model, attempt, images, usage, priced, outcome, started);

        try
        {
            var output = await agent.RunAsync(client, request.Messages, opts.MaxOutputTokens, ct);
            var priced = pricing.Price(model, output.Usage.InputTokens, output.Usage.CachedInputTokens, output.Usage.OutputTokens);
            output = output with { Usage = output.Usage with { CostUsd = priced?.CostUsd } };
            Record(output.Usage, priced, "ok");
            // Tokens are on the gen_ai chat span beneath this one (gen_ai.usage.*) and totalled on the run span.
            if (output.Usage.CostUsd is { } cost) span?.SetTag("intake.extraction.cost_usd", (double)cost);
            span?.SetTag("intake.extraction.result", Telemetry.Truncate(System.Text.Json.JsonSerializer.Serialize(output.Result, ResultJson)));
            return (output, model);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Record(null, null, "timeout");
            var timedOut = new IntakeFailureException(FailureKind.Timeout, "the extraction service timed out");
            span.RecordFailure(timedOut);
            throw timedOut;
        }
        catch (IntakeFailureException ex)
        {
            // The agent refused the answer (cut off, filtered, unreadable), but the provider billed the call.
            var priced = ex.Usage is { } billed ? pricing.Price(model, billed.InputTokens, billed.CachedInputTokens, billed.OutputTokens) : null;
            Record(ex.Usage is null ? null : ex.Usage with { CostUsd = priced?.CostUsd }, priced, "http_" + ServiceProblems.StatusOf(ex.Kind));
            span.RecordFailure(ex);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && errors.Map(ex) is { } mapped)
        {
            Record(null, null, "http_" + ServiceProblems.StatusOf(mapped.Kind));
            span.RecordFailure(mapped);
            throw mapped;
        }
        catch (OperationCanceledException)
        {
            Record(null, null, "cancelled");
            throw;
        }
    }

    /// <summary>Counts the call in the metrics, logs it, and adds it to the calls reported with the answer.</summary>
    private void RecordCall(List<ModelCall> calls, string agentName, string model, int attempt, int images, ExtractionUsage? usage,
        PricedUsage? priced, string outcome, long startedTimestamp)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
        if (usage is not null)
            metrics.RecordLlmCall(model, usage, priced?.CostUsd);
        // Counts only: never names, values or quotes.
        logger.ModelCall(outcome, model, attempt, usage?.InputTokens, usage?.CachedInputTokens, usage?.OutputTokens,
            priced is null ? "unknown" : priced.CostUsd.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture));
        calls.Add(new ModelCall(DateTimeOffset.UtcNow, agentName, model, attempt, images, usage, priced?.PriceKey, outcome, (long)elapsed.TotalMilliseconds));
    }
}

public static class ExtractionServiceExtensions
{
    public static IServiceCollection AddExtraction(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ExtractionOptions>()
            .Bind(configuration.GetSection(ExtractionOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<PricingOptions>().Bind(configuration.GetSection(PricingOptions.Section));
        services.AddSingleton<ModelPricing>();
        services.AddSingleton<ExtractionMetrics>();
        services.AddSingleton(sp => ExtractionClientFactory.Create(sp.GetRequiredService<IOptions<ExtractionOptions>>().Value, sp.GetRequiredService<ExtractionMetrics>()));
        services.AddSingleton<IExtractionErrorMapper, OpenAiExtractionErrorMapper>();
        services.AddSingleton<IExtractionAgent, LoanApplicationAgent>();
        services.AddSingleton<IExtractionAgent, Ucc1Agent>();
        services.AddSingleton<IExtractionAgent, Ucc3Agent>();
        services.AddSingleton<IExtractionAgent, SecurityAgreementAgent>();
        services.AddSingleton<IExtractionAgent, GenericLendingAgent>();
        services.AddSingleton<ExtractionOrchestrator>();
        return services;
    }
}

using System.Diagnostics;
using LayaSample.Api.Models;
using LayaSample.Rendering;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace LayaSample.Api.Services.Documents.Extraction;

public interface IDocumentExtractor
{
    bool IsConfigured { get; }
    Task<ExtractDocumentResponse> ExtractAsync(string fileName, DocumentClassification classification, PreparedDocument document, CancellationToken ct);
}

/// <summary>
/// Picks the agent for a document from its text (<see cref="LendingTypeDetector"/>), sends it as page images when it has
/// them and as text otherwise, then checks the answer. One model call per document; a second only when a type agent
/// found next to nothing and the generic agent gets to look.
/// </summary>
public sealed class DocumentExtractor(
    IOptions<ExtractionOptions> options,
    ExtractionClients clients,
    IExtractionErrorMapper errors,
    IEnumerable<IExtractionAgent> agents,
    DocumentMetrics metrics,
    ILogger<DocumentExtractor> logger) : IDocumentExtractor
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

    public async Task<ExtractDocumentResponse> ExtractAsync(string fileName, DocumentClassification classification, PreparedDocument document, CancellationToken ct)
    {
        using var span = Telemetry.Source.StartActivity("laya.extraction");
        var started = Stopwatch.GetTimestamp();
        metrics.ExtractionStarted();
        var outcome = "ok";
        var agentName = "none";
        var modelName = "none";
        try
        {
            var response = await ExtractCoreAsync(fileName, classification, document, span, ct);
            (agentName, modelName) = (response.Agent, response.Model);
            return response;
        }
        catch (Exception ex)
        {
            outcome = ex switch
            {
                ExtractionException e when e.IsMisconfiguration => "misconfigured",
                ExtractionException e => "http_" + e.StatusCode,
                OperationCanceledException => "cancelled",
                _ => "error"
            };
            span.RecordFailure(ex, outcome);
            throw;
        }
        finally
        {
            metrics.ExtractionFinished();
            metrics.RecordExtractionRun(agentName, modelName, Stopwatch.GetElapsedTime(started), outcome);
            span?.SetTag("laya.outcome", outcome);
        }
    }

    private async Task<ExtractDocumentResponse> ExtractCoreAsync(string fileName, DocumentClassification classification, PreparedDocument document,
        Activity? span, CancellationToken ct)
    {
        var opts = options.Value;
        var detected = LendingTypeDetector.Detect(document);
        var agent = _agents[detected];  // Other maps to the generic agent
        var warnings = new List<string>();
        span?.SetTag("laya.extraction.detected_type", detected.ToString());
        span?.SetTag("laya.extraction.initial_agent", agent.Name);

        var (output, model) = await RunAsync(agent, document, opts, warnings, ct);
        var checkWarnings = ExtractionValidator.Validate(output.Result, document, classification.PageCount);

        if (detected != LendingDocumentType.Other && ExtractionValidator.IsMostlyEmpty(output.Result))
        {
            logger.LogInformation("{Agent} found almost nothing; retrying with the generic agent", agent.Name);
            span?.AddEvent(new ActivityEvent("generic-agent-retry", tags: new ActivityTagsCollection { ["laya.extraction.first_agent"] = agent.Name }));
            span?.SetTag("laya.extraction.generic_retry", true);
            var generic = _agents[LendingDocumentType.Other];
            var retryWarnings = new List<string>();
            var (retry, retryModel) = await RunAsync(generic, document, opts, retryWarnings, ct);
            var retryChecks = ExtractionValidator.Validate(retry.Result, document, classification.PageCount);
            warnings.Add($"the document did not look like a {detected}; the generic agent was used instead");
            output = retry with { Usage = new ExtractionUsage(output.Usage.InputTokens + retry.Usage.InputTokens, output.Usage.OutputTokens + retry.Usage.OutputTokens) };
            (checkWarnings, agent, model) = (retryChecks, generic, retryModel);
            warnings.AddRange(retryWarnings);
        }
        warnings.AddRange(checkWarnings);
        output = output with { Result = output.Result with { OtherFields = ExtractionValidator.CleanOtherFields(output.Result.OtherFields, classification.PageCount) } };

        metrics.RecordExtraction(agent.Name, output.Result.DocumentType.ToString(), model, output.Usage.InputTokens, output.Usage.OutputTokens);
        var allFields = ExtractionValidator.Fields(output.Result).ToList();
        var found = allFields.Count(f => !string.IsNullOrWhiteSpace(f.Value));
        metrics.RecordFields(agent.Name, found, allFields.Count);
        metrics.RecordWarnings(agent.Name, warnings.Count);
        span?.SetTag("laya.extraction.agent", agent.Name);
        span?.SetTag("laya.extraction.model", model);
        span?.SetTag("laya.extraction.document_type", output.Result.DocumentType.ToString());
        span?.SetTag("laya.extraction.fields_found", found);
        span?.SetTag("laya.extraction.fields_total", allFields.Count);
        span?.SetTag("laya.extraction.other_fields", output.Result.OtherFields?.Count ?? 0);
        span?.SetTag("laya.extraction.warnings", warnings.Count);
        span?.SetTag("laya.extraction.input_tokens", output.Usage.InputTokens);
        span?.SetTag("laya.extraction.output_tokens", output.Usage.OutputTokens);
        if (warnings.Count > 0)
            logger.LogWarning("Extraction of {Type} finished with {Count} warning(s): {Warnings}", output.Result.DocumentType, warnings.Count, warnings);
        // Counts and kinds only, never names, values or quotes: all of them can be personal data.
        logger.LogInformation("Extracted {Type} with {Agent} ({Model}): {Found}/{Total} fields, {In} in / {Out} out tokens",
            output.Result.DocumentType, agent.Name, model, ExtractionValidator.Fields(output.Result).Count(f => !string.IsNullOrWhiteSpace(f.Value)),
            ExtractionValidator.Fields(output.Result).Count(), output.Usage.InputTokens, output.Usage.OutputTokens);

        return new ExtractDocumentResponse(fileName, output.Result.DocumentType, agent.Type == LendingDocumentType.Other ? "model" : "keywords",
            agent.Name, model, output.Result, output.Usage, warnings);
    }

    private async Task<(AgentOutput Output, string Model)> RunAsync(IExtractionAgent agent, PreparedDocument document,
        ExtractionOptions opts, List<string> warnings, CancellationToken ct)
    {
        var request = ExtractionMessageBuilder.Build(agent.SystemPrompt, document, opts);
        warnings.AddRange(request.Warnings);
        var client = request.UsesImages ? clients.Vision! : clients.Text!;
        var model = request.UsesImages ? opts.VisionModel : opts.TextModel;
        using var span = Telemetry.Source.StartActivity("laya.extract");
        // This is a technology demonstrator, so prompts and answers are recorded to debug extraction quality. Page
        // images are not (megabytes of base64): only their count and size. Do not carry this over to real documents.
        span?.SetTag("laya.prompt.system", Telemetry.Truncate(agent.SystemPrompt));
        span?.SetTag("laya.prompt.document", Telemetry.Truncate(DescribeDocument(request.Messages)));
        span?.SetTag("laya.prompt.images", request.Messages.SelectMany(m => m.Contents).OfType<DataContent>().Count());
        span?.SetTag("laya.extraction.agent", agent.Name);
        span?.SetTag("laya.extraction.model", model);
        span?.SetTag("laya.extraction.uses_images", request.UsesImages);
        try
        {
            var output = await agent.RunAsync(client, request.Messages, opts.MaxOutputTokens, ct);
            span?.SetTag("laya.extraction.input_tokens", output.Usage.InputTokens);
            span?.SetTag("laya.extraction.output_tokens", output.Usage.OutputTokens);
            span?.SetTag("laya.extraction.result", Telemetry.Truncate(System.Text.Json.JsonSerializer.Serialize(output.Result, ResultJson)));
            return (output, model);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            span?.SetStatus(ActivityStatusCode.Error, "timed out");
            throw new ExtractionException(StatusCodes.Status504GatewayTimeout, "the extraction service timed out");
        }
        catch (Exception ex) when (ex is not ExtractionException and not OperationCanceledException && errors.Map(ex) is { } mapped)
        {
            span?.SetStatus(ActivityStatusCode.Error, mapped.Message);
            throw mapped;
        }
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
        services.AddSingleton(sp => ExtractionClientFactory.Create(sp.GetRequiredService<IOptions<ExtractionOptions>>().Value, sp.GetRequiredService<DocumentMetrics>()));
        services.AddSingleton<IExtractionErrorMapper, OpenAiExtractionErrorMapper>();
        services.AddSingleton<IExtractionAgent, LoanApplicationAgent>();
        services.AddSingleton<IExtractionAgent, Ucc1Agent>();
        services.AddSingleton<IExtractionAgent, Ucc3Agent>();
        services.AddSingleton<IExtractionAgent, SecurityAgreementAgent>();
        services.AddSingleton<IExtractionAgent, GenericLendingAgent>();
        services.AddSingleton<IDocumentExtractor, DocumentExtractor>();
        return services;
    }
}

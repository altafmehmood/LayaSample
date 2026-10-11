using LayaSample.Api.Models;
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

    public async Task<ExtractDocumentResponse> ExtractAsync(string fileName, DocumentClassification classification, PreparedDocument document, CancellationToken ct)
    {
        var opts = options.Value;
        var detected = LendingTypeDetector.Detect(document);
        var agent = _agents[detected];  // Other maps to the generic agent
        var warnings = new List<string>();

        var (output, model) = await RunAsync(agent, document, opts, warnings, ct);
        var checkWarnings = ExtractionValidator.Validate(output.Result, document, classification.PageCount);

        if (detected != LendingDocumentType.Other && ExtractionValidator.IsMostlyEmpty(output.Result))
        {
            logger.LogInformation("{Agent} found almost nothing; retrying with the generic agent", agent.Name);
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
        try
        {
            return (await agent.RunAsync(client, request.Messages, opts.MaxOutputTokens, ct), model);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ExtractionException(StatusCodes.Status504GatewayTimeout, "the extraction service timed out");
        }
        catch (Exception ex) when (ex is not ExtractionException and not OperationCanceledException && errors.Map(ex) is { } mapped)
        {
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
        services.AddSingleton(sp => ExtractionClientFactory.Create(sp.GetRequiredService<IOptions<ExtractionOptions>>().Value));
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

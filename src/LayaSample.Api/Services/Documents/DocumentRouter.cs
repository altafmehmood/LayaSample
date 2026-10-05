using LayaSample.Api.Models;

namespace LayaSample.Api.Services.Documents;

/// <summary>A downstream AI agent that handles documents classified to a given route.</summary>
public interface IDocumentAgent
{
    string Route { get; }
    Task<AgentResult> HandleAsync(DocumentClassification classification, PreparedDocument document, CancellationToken ct = default);
}

public interface IDocumentRouter
{
    Task<AgentResult> RouteAsync(DocumentClassification classification, PreparedDocument document, CancellationToken ct = default);
}

public sealed class DocumentRouter(IEnumerable<IDocumentAgent> agents) : IDocumentRouter
{
    private readonly Dictionary<string, IDocumentAgent> _agents = agents.ToDictionary(a => a.Route, StringComparer.OrdinalIgnoreCase);

    public Task<AgentResult> RouteAsync(DocumentClassification classification, PreparedDocument document, CancellationToken ct = default) =>
        _agents.TryGetValue(classification.Route, out var agent)
            ? agent.HandleAsync(classification, document, ct)
            : Task.FromResult(new AgentResult(classification.Route, "unrouted", "no agent registered for this route"));
}

/// <summary>Placeholder until a real agent is registered for the route.</summary>
public sealed class StubDocumentAgent(string route) : IDocumentAgent
{
    public string Route { get; } = route;

    public Task<AgentResult> HandleAsync(DocumentClassification classification, PreparedDocument document, CancellationToken ct = default) =>
        Task.FromResult(new AgentResult(Route, "stub", $"{document.Parts.Count} part(s) ready as {document.Strategy}; no agent implemented yet"));
}

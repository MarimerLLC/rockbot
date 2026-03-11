namespace RockBot.Host;

/// <summary>
/// A ranked candidate result from <see cref="IServiceSearchIndex.Search"/>.
/// </summary>
public sealed record ServiceSearchCandidate
{
    /// <summary>Agent name (A2A) or server name (MCP).</summary>
    public required string Id { get; init; }

    /// <summary>"a2a" or "mcp" — determines which tool to use for interaction.</summary>
    public required string Type { get; init; }

    /// <summary>LLM-generated or fallback description of this service's purpose.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// For A2A: top skill IDs. For MCP: top tool names.
    /// Provides an immediate scouting report without needing to call get_agent_details or mcp_get_service_details.
    /// </summary>
    public required IReadOnlyList<string> TopItems { get; init; }

    /// <summary>Relevance score normalized to [0, 1]. Higher = better match to the query.</summary>
    public double RelevanceScore { get; init; }
}

/// <summary>
/// Unified, BM25-searchable index of all known A2A agents and MCP servers.
/// Implementations read live from the in-memory agent directory and MCP server index.
/// </summary>
public interface IServiceSearchIndex
{
    /// <summary>
    /// BM25-ranked search across all known services (agents + MCP servers).
    /// Returns an empty list if no services are indexed or no query terms match any document.
    /// </summary>
    IReadOnlyList<ServiceSearchCandidate> Search(string query, int maxResults = 3);
}

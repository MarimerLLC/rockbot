using System.Text;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Tools.Mcp;

namespace RockBot.ServiceSearch;

/// <summary>
/// Unified BM25-searchable index over all known A2A agents and MCP servers.
/// Reads live from the in-memory <see cref="IAgentDirectory"/> and <see cref="McpServerIndex"/>
/// on each search — no separate cache layer needed since both are already singletons.
/// <para>
/// Ranking stays per service. With typed MCP tools on (#612), an MCP candidate's top items are the
/// typed <c>{server}__{tool}</c> names that best fit the query (<see cref="McpToolSearch"/>), so a
/// hint can say which tool to call rather than send the model to <c>mcp_get_service_details</c>.
/// Tool-level search across every server is <c>mcp_find_tools</c>.
/// </para>
/// </summary>
public sealed class ServiceSearchIndex(
    IAgentDirectory agentDirectory,
    McpServerIndex mcpServerIndex,
    McpTypedToolSurface? typedTools = null) : IServiceSearchIndex
{
    public IReadOnlyList<ServiceSearchCandidate> Search(string query, int maxResults = 3)
    {
        var docs = BuildDocuments();
        if (docs.Count == 0) return [];

        var ranked = Bm25Ranker.RankWithScores(docs, static d => d.IndexText, query);
        if (ranked.Count == 0) return [];

        double maxScore = ranked[0].Score;
        if (maxScore <= 0) return [];

        return ranked
            .Take(maxResults)
            .Select(r =>
            {
                var typed = TypedTopItems(r.Item, query);
                return new ServiceSearchCandidate
                {
                    Id = r.Item.Id,
                    Type = r.Item.Type,
                    Summary = r.Item.Summary,
                    TopItems = typed ?? r.Item.TopItems,
                    TopItemsAreTypedTools = typed is not null,
                    RelevanceScore = Math.Round(r.Score / maxScore, 2)
                };
            })
            .ToList();
    }

    private IReadOnlyList<string>? TypedTopItems(ServiceIndexDocument doc, string query)
    {
        if (typedTools is null || doc.Type != "mcp")
            return null;

        var server = mcpServerIndex.Servers.FirstOrDefault(s => s.ServerName == doc.Id);
        if (server is null)
            return null;

        var typed = typedTools.TypedNamesFor(server, query, max: 3);
        return typed.Count > 0 ? typed : null;
    }

    private List<ServiceIndexDocument> BuildDocuments()
    {
        var docs = new List<ServiceIndexDocument>();

        foreach (var entry in agentDirectory.GetAllEntries())
        {
            var card = entry.Card;
            var text = new StringBuilder();
            text.Append(card.AgentName).Append(' ');
            if (!string.IsNullOrWhiteSpace(card.Description))
                text.Append(card.Description).Append(' ');
            if (!string.IsNullOrWhiteSpace(entry.LlmSummary))
                text.Append(entry.LlmSummary).Append(' ');
            foreach (var skill in card.Skills ?? [])
            {
                text.Append(skill.Id).Append(' ');
                text.Append(skill.Name).Append(' ');
                if (!string.IsNullOrWhiteSpace(skill.Description))
                    text.Append(skill.Description).Append(' ');
                foreach (var tag in skill.Tags ?? [])
                    text.Append(tag).Append(' ');
                foreach (var example in skill.Examples ?? [])
                    text.Append(example).Append(' ');
            }

            docs.Add(new ServiceIndexDocument
            {
                Id = card.AgentName,
                Type = "a2a",
                Summary = entry.LlmSummary ?? card.Description ?? $"Agent: {card.AgentName}",
                IndexText = text.ToString(),
                TopItems = (card.Skills ?? []).Take(3).Select(static s => s.Id).ToList()
            });
        }

        foreach (var server in mcpServerIndex.Servers)
        {
            var text = new StringBuilder();
            text.Append(server.ServerName).Append(' ');
            if (!string.IsNullOrWhiteSpace(server.DisplayName))
                text.Append(server.DisplayName).Append(' ');
            if (!string.IsNullOrWhiteSpace(server.Summary))
                text.Append(server.Summary).Append(' ');
            foreach (var tool in server.ToolNames)
                text.Append(tool).Append(' ');

            docs.Add(new ServiceIndexDocument
            {
                Id = server.ServerName,
                Type = "mcp",
                Summary = server.Summary ?? server.DisplayName ?? server.ServerName,
                IndexText = text.ToString(),
                TopItems = server.ToolNames.Take(3).ToList()
            });
        }

        return docs;
    }
}

internal sealed record ServiceIndexDocument
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string Summary { get; init; }
    public required string IndexText { get; init; }
    public required IReadOnlyList<string> TopItems { get; init; }
}

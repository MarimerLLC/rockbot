using System.Collections.Concurrent;

namespace RockBot.A2A;

/// <summary>
/// Thread-safe in-memory agent directory. Updated by <see cref="AgentDiscoveryService"/>
/// when agent cards arrive on the discovery topic.
/// </summary>
internal sealed class AgentDirectory : IAgentDirectory
{
    private readonly ConcurrentDictionary<string, AgentCard> _agents = new(StringComparer.OrdinalIgnoreCase);

    public AgentCard? GetAgent(string agentName) =>
        _agents.TryGetValue(agentName, out var card) ? card : null;

    public IReadOnlyList<AgentCard> GetAllAgents() =>
        _agents.Values.ToList();

    public IReadOnlyList<AgentCard> FindBySkill(string skillId) =>
        _agents.Values
            .Where(c => c.Skills?.Any(s => string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();

    internal void AddOrUpdate(AgentCard card) =>
        _agents[card.AgentName] = card;
}

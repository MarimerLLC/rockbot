namespace RockBot.A2A;

/// <summary>
/// Read-only directory of known agents, populated by discovery broadcasts.
/// </summary>
public interface IAgentDirectory
{
    AgentCard? GetAgent(string agentName);
    IReadOnlyList<AgentCard> GetAllAgents();
    IReadOnlyList<AgentCard> FindBySkill(string skillId);
}

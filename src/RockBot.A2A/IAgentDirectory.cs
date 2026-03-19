namespace RockBot.A2A;

/// <summary>
/// Directory of known agents, populated by discovery broadcasts and manual registration.
/// </summary>
public interface IAgentDirectory
{
    AgentCard? GetAgent(string agentName);
    IReadOnlyList<AgentCard> GetAllAgents();
    IReadOnlyList<AgentCard> FindBySkill(string skillId);

    /// <summary>
    /// Returns all directory entries including last-seen timestamps.
    /// Used by the persistence layer and for display purposes.
    /// </summary>
    IReadOnlyList<AgentDirectoryEntry> GetAllEntries();

    /// <summary>
    /// Adds or updates an agent in the directory and schedules persistence.
    /// </summary>
    void AddOrUpdate(AgentCard card);

    /// <summary>
    /// Removes an agent from the directory. Well-known agents are not removed.
    /// </summary>
    void Remove(string agentName);
}

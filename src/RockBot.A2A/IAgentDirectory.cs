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

    /// <summary>
    /// Updates the LLM-generated summary for an agent. No-op if the agent is unknown.
    /// </summary>
    void SetSummary(string agentName, string summary);

    /// <summary>
    /// Re-fetches every well-known peer's <c>/.well-known/agent-card.json</c> and merges
    /// the result into the directory. Seeds with an explicit <see cref="AgentCard.Skills"/>
    /// array are treated as offline overrides and skipped.
    /// </summary>
    Task<IReadOnlyList<AgentCardRefreshResult>> RefreshAllWellKnownAsync(CancellationToken ct);

    /// <summary>
    /// Re-fetches a single agent's <c>/.well-known/agent-card.json</c>. Returns a result
    /// describing what happened (refreshed / skipped / not-found / error) so the caller
    /// can decide whether to regenerate the cached LLM summary.
    /// </summary>
    Task<AgentCardRefreshResult> RefreshAgentCardAsync(string agentName, CancellationToken ct);
}

/// <summary>
/// Outcome of a single agent-card refresh attempt.
/// </summary>
/// <param name="AgentName">Agent the refresh targeted.</param>
/// <param name="Refreshed">True when a remote fetch happened and merged successfully.</param>
/// <param name="SkillsChanged">True when the skill-id set differs from the pre-refresh entry.</param>
/// <param name="Reason">Human-readable note for skipped/not-found/error cases.</param>
public sealed record AgentCardRefreshResult(
    string AgentName,
    bool Refreshed,
    bool SkillsChanged,
    string? Reason);

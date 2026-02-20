namespace RockBot.Host;

/// <summary>
/// Loads an <see cref="AgentProfile"/> from its backing store.
/// </summary>
public interface IAgentProfileProvider
{
    /// <summary>
    /// Loads and parses the agent profile documents.
    /// </summary>
    Task<AgentProfile> LoadAsync(CancellationToken cancellationToken = default);
}

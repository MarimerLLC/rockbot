namespace RockBot.A2A;

/// <summary>
/// Persistent store for per-caller trust entries. Implementations must be
/// thread-safe — concurrent A2A requests may read/write simultaneously.
/// </summary>
public interface IAgentTrustStore
{
    /// <summary>
    /// Returns the trust entry for <paramref name="agentId"/>, creating a new
    /// entry at <see cref="AgentTrustLevel.Observe"/> if none exists.
    /// </summary>
    Task<AgentTrustEntry> GetOrCreateAsync(string agentId, CancellationToken ct);

    /// <summary>
    /// Persists an updated trust entry. The entry is matched by <see cref="AgentTrustEntry.AgentId"/>.
    /// </summary>
    Task UpdateAsync(AgentTrustEntry entry, CancellationToken ct);

    /// <summary>
    /// Returns all known trust entries.
    /// </summary>
    Task<IReadOnlyList<AgentTrustEntry>> ListAsync(CancellationToken ct);
}

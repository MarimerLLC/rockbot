namespace RockBot.Host;

/// <summary>
/// Persistent store for skill invocation events.
/// Entries are written when a skill is retrieved and queried by the dreaming system
/// to annotate skill consolidation and optimization passes.
/// </summary>
public interface ISkillUsageStore
{
    /// <summary>Appends a skill invocation event to the store.</summary>
    Task AppendAsync(SkillInvocationEvent evt, CancellationToken ct = default);

    /// <summary>Returns all invocation events for the specified session.</summary>
    Task<IReadOnlyList<SkillInvocationEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Returns invocation events recorded on or after <paramref name="since"/>,
    /// ordered by timestamp ascending, capped at <paramref name="maxResults"/>.
    /// </summary>
    Task<IReadOnlyList<SkillInvocationEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default);
}

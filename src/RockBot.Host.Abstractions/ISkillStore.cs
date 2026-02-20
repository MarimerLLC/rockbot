namespace RockBot.Host;

/// <summary>
/// Persistent store for agent skills — named markdown procedure documents the agent
/// can create, consult, and refine over time.
/// </summary>
public interface ISkillStore
{
    /// <summary>Creates or replaces a skill.</summary>
    Task SaveAsync(Skill skill);

    /// <summary>Returns the skill by name, or <c>null</c> if not found.</summary>
    Task<Skill?> GetAsync(string name);

    /// <summary>Returns all skills ordered by name.</summary>
    Task<IReadOnlyList<Skill>> ListAsync();

    /// <summary>Removes a skill. No-op if the skill does not exist.</summary>
    Task DeleteAsync(string name);

    /// <summary>
    /// Returns skills ranked by BM25 relevance against <paramref name="query"/>.
    /// Skills with no matching terms are excluded.
    /// Returns at most <paramref name="maxResults"/> entries.
    /// </summary>
    Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default);
}

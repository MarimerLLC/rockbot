namespace RockBot.Host;

/// <summary>
/// Persistent store for agent skills — named markdown procedure documents the agent
/// can create, consult, and refine over time.
/// </summary>
public interface ISkillStore
{
    /// <summary>Creates or replaces a skill (metadata and markdown only; does not touch resource files).</summary>
    Task SaveAsync(Skill skill);

    /// <summary>
    /// Saves a skill together with its sub-resource files as an atomic bundle.
    /// Resource files are written to the skill's subfolder, orphaned files from previous saves
    /// are removed, and the manifest is rebuilt from the provided resources.
    /// When <paramref name="resources"/> is <c>null</c> or empty the call is equivalent to
    /// <see cref="SaveAsync(Skill)"/>.
    /// </summary>
    Task SaveAsync(Skill skill, IReadOnlyList<SkillResourceInput>? resources) => SaveAsync(skill);

    /// <summary>Returns the skill by name, or <c>null</c> if not found.</summary>
    Task<Skill?> GetAsync(string name);

    /// <summary>Returns all skills ordered by name.</summary>
    Task<IReadOnlyList<Skill>> ListAsync();

    /// <summary>Removes a skill and its resource subfolder. No-op if the skill does not exist.</summary>
    Task DeleteAsync(string name);

    /// <summary>
    /// Returns the text content of a named sub-resource file for a skill, or <c>null</c>
    /// if the skill or the resource does not exist.
    /// </summary>
    /// <param name="skillName">The skill name (e.g. <c>plan-meeting</c>).</param>
    /// <param name="filename">Simple filename of the resource (e.g. <c>script.py</c>).</param>
    Task<string?> GetResourceAsync(string skillName, string filename) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Returns skills ranked by BM25 relevance against <paramref name="query"/>.
    /// Skills with no matching terms are excluded.
    /// Returns at most <paramref name="maxResults"/> entries.
    /// </summary>
    /// <param name="query">The search query text.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="queryEmbedding">
    /// Pre-computed query embedding vector. When provided, the store skips generating
    /// its own query embedding — avoiding redundant embedding endpoint calls.
    /// </param>
    Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null);
}

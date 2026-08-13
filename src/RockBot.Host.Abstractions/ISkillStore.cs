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

    /// <summary>
    /// Replaces an exact piece of text inside an existing skill's markdown body, leaving the
    /// rest of the document and every other field untouched.
    /// </summary>
    /// <param name="name">Skill to edit.</param>
    /// <param name="oldText">Exact text to find. Must be non-empty.</param>
    /// <param name="newText">Replacement text. May be empty to delete the match.</param>
    /// <param name="replaceAll">
    /// When <c>true</c>, replaces every occurrence. When <c>false</c>, more than one
    /// occurrence is refused rather than guessed at.
    /// </param>
    /// <remarks>
    /// Distinct from <see cref="SaveAsync(Skill)"/> in more than granularity: the save path
    /// clears the summary and regenerates it with a background LLM call, so adding one pitfall
    /// note to a long procedure costs a model round-trip and leaves the skill index blank
    /// until it returns. An edit keeps the summary, the manifest, and
    /// <see cref="Skill.CreatedAt"/>, and moves only the body and
    /// <see cref="Skill.UpdatedAt"/>.
    /// </remarks>
    Task<ContentEditResult> EditContentAsync(string name, string oldText, string newText, bool replaceAll = false)
        => Task.FromResult(ContentEditResult.NotSupported);

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
    /// Adds (or replaces) a single sub-resource on an existing skill without disturbing
    /// any other resources. The skill's body and other manifest entries are preserved.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="SaveAsync(Skill, IReadOnlyList{SkillResourceInput}?)"/>, which
    /// rebuilds the manifest from the supplied set and prunes orphans, this method is
    /// strictly additive: pass a single resource and it is appended to the manifest, or
    /// — if a manifest entry with the same filename exists — its body and metadata are
    /// replaced in place.
    /// Returns false when the named skill does not exist; promotion paths require the
    /// caller to ensure the parent skill exists before attaching an asset.
    /// </remarks>
    /// <param name="skillName">The skill to attach the resource to.</param>
    /// <param name="resource">The resource metadata and content.</param>
    /// <param name="manifestEntry">
    /// Optional pre-built manifest entry to persist. When provided, its
    /// <see cref="SkillResource.Provisional"/>, <see cref="SkillResource.CreatedAt"/>,
    /// <see cref="SkillResource.VerifyHint"/>, and <see cref="SkillResource.DefinitionHash"/>
    /// are stored verbatim. When null, the manifest entry is built from
    /// <paramref name="resource"/>'s fields.
    /// </param>
    Task<bool> AttachResourceAsync(
        string skillName,
        SkillResourceInput resource,
        SkillResource? manifestEntry = null) => Task.FromResult(false);

    /// <summary>
    /// Removes a single sub-resource (manifest entry plus on-disk file) from a skill.
    /// No-op when the skill or the named resource does not exist. Other resources are
    /// preserved; the skill body is untouched.
    /// </summary>
    Task<bool> RemoveResourceAsync(string skillName, string filename) =>
        Task.FromResult(false);

    /// <summary>
    /// Replaces a single manifest entry in-place — preserves the on-disk body and all
    /// other entries, but updates the metadata fields (<see cref="SkillResource.Provisional"/>,
    /// <see cref="SkillResource.Description"/>, <see cref="SkillResource.VerifyHint"/>, etc.)
    /// of the entry whose <see cref="SkillResource.Filename"/> matches.
    /// Returns false when the skill or the named entry does not exist.
    /// </summary>
    Task<bool> UpdateResourceMetadataAsync(string skillName, SkillResource updated) =>
        Task.FromResult(false);

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

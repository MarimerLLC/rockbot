namespace RockBot.Host;

/// <summary>
/// Criteria for searching long-term memory entries.
/// All specified criteria are combined with AND logic.
/// </summary>
/// <param name="Query">
/// In <see cref="MemorySearchMode.Hybrid"/> (default), a case-insensitive keyword/phrase to rank against.
/// In <see cref="MemorySearchMode.Regex"/>, a .NET regex pattern matched against the entry's
/// memory path name (<c>{category}/{id}</c> or <c>{id}</c>) plus its content, tags, and category words.
/// </param>
/// <param name="Category">Category prefix to match (e.g. "project-context" matches "project-context/rockbot").</param>
/// <param name="Tags">Tags that entries must contain (all specified tags must be present).</param>
/// <param name="CreatedAfter">Only include entries created after this time.</param>
/// <param name="CreatedBefore">Only include entries created before this time.</param>
/// <param name="MaxResults">Maximum number of results to return. Defaults to 20.</param>
/// <param name="QueryEmbedding">
/// Pre-computed query embedding vector. When provided, stores skip generating their own
/// query embedding — avoiding redundant calls to the embedding endpoint when multiple
/// searches share the same query text (e.g. during context building). Ignored in
/// <see cref="MemorySearchMode.Regex"/>.
/// </param>
/// <param name="Mode">Search backend selector. Defaults to <see cref="MemorySearchMode.Hybrid"/>.</param>
/// <param name="RegexCaseSensitive">
/// When <see cref="Mode"/> is <see cref="MemorySearchMode.Regex"/>, controls case sensitivity of the regex.
/// Default <c>false</c> mirrors Claude Code's Grep tool. Ignored in hybrid mode.
/// </param>
/// <param name="IncludeSuperseded">
/// When <c>true</c>, entries with <see cref="MemoryEntry.SupersededBy"/> set are included in
/// search results. Default <c>false</c> hides them from recall, mirroring Phase 3 self-repair
/// semantics. Used by audit tooling and the dream contradiction sweep that need to inspect
/// the full corpus.
/// </param>
public sealed record MemorySearchCriteria(
    string? Query = null,
    string? Category = null,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? CreatedAfter = null,
    DateTimeOffset? CreatedBefore = null,
    int MaxResults = 20,
    float[]? QueryEmbedding = null,
    MemorySearchMode Mode = MemorySearchMode.Hybrid,
    bool RegexCaseSensitive = false,
    bool IncludeSuperseded = false);

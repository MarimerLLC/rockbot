namespace RockBot.Host;

/// <summary>
/// Criteria for searching long-term memory entries.
/// All specified criteria are combined with AND logic.
/// </summary>
/// <param name="Query">Case-insensitive substring to match against content.</param>
/// <param name="Category">Category prefix to match (e.g. "project-context" matches "project-context/rockbot").</param>
/// <param name="Tags">Tags that entries must contain (all specified tags must be present).</param>
/// <param name="CreatedAfter">Only include entries created after this time.</param>
/// <param name="CreatedBefore">Only include entries created before this time.</param>
/// <param name="MaxResults">Maximum number of results to return. Defaults to 20.</param>
public sealed record MemorySearchCriteria(
    string? Query = null,
    string? Category = null,
    IReadOnlyList<string>? Tags = null,
    DateTimeOffset? CreatedAfter = null,
    DateTimeOffset? CreatedBefore = null,
    int MaxResults = 20);

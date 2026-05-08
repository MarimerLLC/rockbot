namespace RockBot.Host;

/// <summary>
/// A single entry in long-term agent memory.
/// </summary>
/// <param name="Id">Unique identifier for this memory entry.</param>
/// <param name="Content">The memory content.</param>
/// <param name="Category">Optional category path (e.g. "user-preferences", "project-context/rockbot"). Maps to subdirectories on disk.</param>
/// <param name="Tags">Tags for filtering and search.</param>
/// <param name="CreatedAt">When the entry was first created (first-seen agent-time).</param>
/// <param name="UpdatedAt">When the entry was last rewritten, if ever. Distinct from LastSeenAt — bumped on any edit, including dream rephrasing.</param>
/// <param name="Metadata">Arbitrary key-value metadata. Well-known keys include "subjectTime", "subjectTimeStart", "subjectTimeEnd" for capturing when the thing the fact is about actually happened.</param>
/// <param name="ImportanceScore">Salience score from 0.0 (trivial) to 1.0 (critical). Defaults to 0.5.</param>
public sealed record MemoryEntry(
    string Id,
    string Content,
    string? Category,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    float ImportanceScore = 0.5f)
{
    /// <summary>
    /// Most recent time a fresh save-event was merged into this fact.
    /// Advances only on real reinforcement (new save merged in, episode re-referenced),
    /// not on dream rephrasing, importance decay, or other record edits.
    /// Defaults to <see cref="CreatedAt"/> for never-reinforced entries and legacy JSON files
    /// that predate this field.
    /// </summary>
    public DateTimeOffset LastSeenAt { get; init; } = CreatedAt;

    /// <summary>
    /// Count of distinct observations consolidated into this entry.
    /// Starts at 1 for a fresh save; merges sum this across source entries.
    /// </summary>
    public int ReinforcementCount { get; init; } = 1;

    /// <summary>
    /// Optional structured predicate that lets readers falsify this entry by re-running
    /// the call it describes. Populated only on entries in the
    /// <c>claim/capability/*</c> category (see <see cref="CapabilityClaimCategories"/>);
    /// always <c>null</c> for general memory entries. Read-side filters in the
    /// agent context builder evaluate this shape before injection.
    /// </summary>
    public VerifyShape? Verify { get; init; }
}

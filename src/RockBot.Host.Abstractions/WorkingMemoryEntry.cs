namespace RockBot.Host;

/// <summary>
/// A single live entry in session-scoped working memory.
/// </summary>
/// <param name="Key">Full-path key (e.g. <c>shared/patrol/todos-latest</c>).</param>
/// <param name="Value">The cached payload.</param>
/// <param name="StoredAt">
/// When the entry was last written. <c>default</c> means unknown — entries restored from a
/// persisted file written before stored-at was recorded carry no timestamp.
/// </param>
/// <param name="ExpiresAt">When the entry's TTL lapses.</param>
/// <param name="Category">Optional grouping category.</param>
/// <param name="Tags">Optional filter tags.</param>
/// <param name="Writer">
/// Who wrote the entry, when known — the namespace of the context that saved it
/// (e.g. <c>patrol/heartbeat-patrol</c>, <c>session/abc123</c>, <c>worker/task1</c>).
/// <c>null</c> for writes from infrastructure that does not identify itself, and for
/// entries persisted before the writer was recorded.
/// </param>
public sealed record WorkingMemoryEntry(
    string Key,
    string Value,
    DateTimeOffset StoredAt,
    DateTimeOffset ExpiresAt,
    string? Category = null,
    IReadOnlyList<string>? Tags = null,
    string? Writer = null)
{
    /// <summary><c>true</c> when <see cref="StoredAt"/> is a real timestamp rather than "unknown".</summary>
    public bool HasStoredAt => StoredAt != default;
}

namespace RockBot.Host;

/// <summary>
/// A single entry in long-term agent memory.
/// </summary>
/// <param name="Id">Unique identifier for this memory entry.</param>
/// <param name="Content">The memory content.</param>
/// <param name="Category">Optional category path (e.g. "user-preferences", "project-context/rockbot"). Maps to subdirectories on disk.</param>
/// <param name="Tags">Tags for filtering and search.</param>
/// <param name="CreatedAt">When the entry was created.</param>
/// <param name="UpdatedAt">When the entry was last updated, if ever.</param>
/// <param name="Metadata">Arbitrary key-value metadata.</param>
public sealed record MemoryEntry(
    string Id,
    string Content,
    string? Category,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

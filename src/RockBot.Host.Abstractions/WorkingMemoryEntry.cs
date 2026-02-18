namespace RockBot.Host;

/// <summary>
/// A single live entry in session-scoped working memory.
/// </summary>
public sealed record WorkingMemoryEntry(
    string Key,
    string Value,
    DateTimeOffset StoredAt,
    DateTimeOffset ExpiresAt);

namespace RockBot.Host;

/// <summary>
/// A single live entry in cross-session shared memory.
/// </summary>
public sealed record SharedMemoryEntry(
    string Key,
    string Value,
    DateTimeOffset StoredAt,
    DateTimeOffset ExpiresAt,
    string? Category = null,
    IReadOnlyList<string>? Tags = null);

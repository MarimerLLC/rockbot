namespace RockBot.Host;

/// <summary>
/// Holds the agent's display name and tracks a version counter that increments
/// on each update. Thread-safe for concurrent readers.
/// When no display name is set, consumers should fall back to <see cref="AgentIdentity.Name"/>.
/// </summary>
public sealed class AgentNameHolder
{
    private volatile string? _displayName;
    private long _version;

    /// <summary>
    /// Current version. Increments on each <see cref="Update"/> call.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// The agent's display name, or null if no custom name has been set.
    /// </summary>
    public string? DisplayName => _displayName;

    /// <summary>
    /// Atomically replaces the display name and increments the version counter.
    /// Pass null to clear the display name (revert to identity name).
    /// </summary>
    public void Update(string? displayName)
    {
        _displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        Interlocked.Increment(ref _version);
    }
}

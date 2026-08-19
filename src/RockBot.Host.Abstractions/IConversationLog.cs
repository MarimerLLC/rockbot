namespace RockBot.Host;

/// <summary>
/// Long-term conversation log used to accumulate turn history for preference inference.
/// </summary>
public interface IConversationLog
{
    /// <summary>Appends a single conversation turn to the log.</summary>
    Task AppendAsync(ConversationLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Reads all entries currently in the log.</summary>
    Task<IReadOnlyList<ConversationLogEntry>> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears the log. Called by the dream pass after processing.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="maxEntries"/> of the most recent entries for
    /// <paramref name="sessionId"/>, in chronological order. When the session has more
    /// than <paramref name="maxEntries"/> turns the oldest are dropped, not the newest.
    /// </summary>
    /// <remarks>
    /// Exists so callers on a user-facing latency path can read one session without
    /// materialising the whole multi-session log the way <see cref="ReadAllAsync"/> does.
    /// The default implementation delegates to <see cref="ReadAllAsync"/> and filters, which
    /// is correct but reads everything; implementations backed by a file or database should
    /// override it with a bounded read.
    /// </remarks>
    async Task<IReadOnlyList<ConversationLogEntry>> ReadSessionAsync(
        string sessionId, int maxEntries, CancellationToken cancellationToken = default)
    {
        if (maxEntries <= 0) return [];

        var all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var matching = all
            .Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(e => e.Timestamp)
            .ToList();

        return matching.Count <= maxEntries
            ? matching
            : matching.GetRange(matching.Count - maxEntries, maxEntries);
    }

    /// <summary>
    /// Returns one <see cref="ConversationLogSessionInfo"/> per session present in the log,
    /// most recently active first.
    /// </summary>
    /// <remarks>
    /// The default implementation delegates to <see cref="ReadAllAsync"/>; implementations
    /// backed by a file or database should override it with a streaming scan.
    /// </remarks>
    async Task<IReadOnlyList<ConversationLogSessionInfo>> ListLoggedSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);

        return all
            .GroupBy(e => e.SessionId, StringComparer.Ordinal)
            .Select(g => new ConversationLogSessionInfo(
                g.Key, g.Count(), g.Min(e => e.Timestamp), g.Max(e => e.Timestamp)))
            .OrderByDescending(s => s.LastTimestamp)
            .ToList();
    }
}

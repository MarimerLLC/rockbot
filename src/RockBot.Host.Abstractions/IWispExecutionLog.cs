namespace RockBot.Host;

/// <summary>
/// Persistent log of wisp pipeline executions.
/// Entries are written after every wisp run (success or failure) and queried
/// by the dream system to detect recurring failure patterns and identify
/// skill improvement candidates.
/// </summary>
public interface IWispExecutionLog
{
    /// <summary>Appends a wisp execution record to the log.</summary>
    Task AppendAsync(WispExecutionRecord record, CancellationToken ct = default);

    /// <summary>
    /// Returns wisp execution records on or after <paramref name="since"/>,
    /// ordered by timestamp ascending, capped at <paramref name="maxResults"/>.
    /// </summary>
    Task<IReadOnlyList<WispExecutionRecord>> QueryRecentAsync(
        DateTimeOffset since, int maxResults, CancellationToken ct = default);

    /// <summary>
    /// Returns recent failed records matching the given definition hash,
    /// for detecting retries. Most recent first.
    /// </summary>
    Task<WispExecutionRecord?> FindRecentFailureAsync(
        string definitionHash, string? sessionId, CancellationToken ct = default);
}

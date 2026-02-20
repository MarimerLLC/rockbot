namespace RockBot.Host;

/// <summary>
/// Persistent store for agent feedback signals.
/// Entries are written by signal capture (correction detection, tool failures, session summaries)
/// and queried by the dreaming system to inform memory consolidation.
/// </summary>
public interface IFeedbackStore
{
    /// <summary>Appends a feedback entry to the store.</summary>
    Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Returns all feedback entries for the specified session.</summary>
    Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns feedback entries recorded on or after <paramref name="since"/>,
    /// ordered by timestamp ascending, capped at <paramref name="maxResults"/>.
    /// </summary>
    Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken cancellationToken = default);
}

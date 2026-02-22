using RockBot.Host;

namespace RockBot.ResearchAgent;

/// <summary>
/// No-op <see cref="IFeedbackStore"/> for the ephemeral research agent.
/// The research agent has no dreaming or memory consolidation pipeline,
/// so feedback signals are silently discarded.
/// </summary>
internal sealed class NullFeedbackStore : IFeedbackStore
{
    public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(
        string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);

    public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(
        DateTimeOffset since, int maxResults, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
}

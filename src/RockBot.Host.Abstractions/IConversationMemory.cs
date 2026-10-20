namespace RockBot.Host;

/// <summary>
/// Ephemeral in-memory store for conversation turns within a session.
/// Lost on restart — consistent with stateless agent design.
/// </summary>
public interface IConversationMemory
{
    /// <summary>
    /// Records a turn in the specified session.
    /// </summary>
    Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all retained turns for the specified session, in chronological order.
    /// </summary>
    Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns turns of the specified live session that were evicted from
    /// <see cref="GetTurnsAsync"/>'s retention window, oldest first. They come before the retained
    /// turns, and are only ever read for recall (#509). Stores that discard evicted turns return
    /// none.
    /// </summary>
    Task<IReadOnlyList<ConversationTurn>> GetArchivedTurnsAsync(string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ConversationTurn>>([]);

    /// <summary>
    /// Clears all turns for the specified session.
    /// </summary>
    Task ClearAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the IDs of all sessions currently held in memory.
    /// </summary>
    Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default);
}

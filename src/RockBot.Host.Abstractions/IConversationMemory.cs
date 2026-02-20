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
    /// Clears all turns for the specified session.
    /// </summary>
    Task ClearAsync(string sessionId, CancellationToken cancellationToken = default);
}

namespace RockBot.UserProxy;

/// <summary>
/// Requests the full conversation history for a session.
/// </summary>
public sealed record ConversationHistoryRequest
{
    public required string SessionId { get; init; }

    /// <summary>
    /// Also return the output of the broadcast sessions (scheduled tasks, background system jobs,
    /// inbound A2A notifications), merged with the session's turns by timestamp. Those replies go to
    /// every connected client, so a thread that shows them live needs them on reload too. Each turn
    /// says which session it came from in <see cref="ConversationHistoryTurn.SessionId"/>.
    /// </summary>
    public bool IncludeBroadcast { get; init; }
}

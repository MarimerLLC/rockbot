namespace RockBot.UserProxy;

/// <summary>
/// A single turn in a replayed conversation history.
/// </summary>
public sealed record ConversationHistoryTurn
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The name of the agent that produced this turn, if applicable.
    /// Used by the UI to restore proper message categorization on history reload.
    /// </summary>
    public string? AgentName { get; init; }
}

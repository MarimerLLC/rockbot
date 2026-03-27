namespace RockBot.Host;

/// <summary>
/// A single turn in a conversation session.
/// </summary>
/// <param name="Role">The role of the participant (e.g. "user", "assistant").</param>
/// <param name="Content">The content of the turn.</param>
/// <param name="Timestamp">When the turn occurred.</param>
public sealed record ConversationTurn(string Role, string Content, DateTimeOffset Timestamp)
{
    /// <summary>
    /// The name of the agent that produced this turn, if applicable.
    /// Used to restore proper UI categorization (subagent, A2A, primary) on history reload.
    /// </summary>
    public string? AgentName { get; init; }
}

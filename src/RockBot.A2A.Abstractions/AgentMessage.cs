namespace RockBot.A2A;

/// <summary>
/// A single turn of communication in an agent-to-agent exchange.
/// </summary>
public sealed record AgentMessage
{
    public required string Role { get; init; }
    public required IReadOnlyList<AgentMessagePart> Parts { get; init; }
}

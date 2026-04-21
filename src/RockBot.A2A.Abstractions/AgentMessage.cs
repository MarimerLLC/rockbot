namespace RockBot.A2A;

/// <summary>
/// A single turn of communication in an agent-to-agent exchange.
/// </summary>
public sealed record AgentMessage
{
    public required string Role { get; init; }
    public required IReadOnlyList<AgentMessagePart> Parts { get; init; }

    /// <summary>
    /// Optional structured metadata associated with this message. Mirrors the
    /// A2A v1 <c>Message.metadata</c> field. Bridges propagate this between the
    /// HTTP boundary and the bus so callers can attach per-message inputs
    /// (URLs, identifiers, selector hints, etc.) alongside the text parts.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

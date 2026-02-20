namespace RockBot.A2A;

/// <summary>
/// A content part within an agent message.
/// </summary>
public sealed record AgentMessagePart
{
    public required string Kind { get; init; }
    public string? Text { get; init; }
    public string? Data { get; init; }
    public string? MimeType { get; init; }
}

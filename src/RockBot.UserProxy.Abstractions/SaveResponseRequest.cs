namespace RockBot.UserProxy;

/// <summary>
/// Request to save an agent response for later reference.
/// </summary>
public sealed record SaveResponseRequest
{
    public required string Label { get; init; }
    public required string Content { get; init; }
    public required string AgentName { get; init; }
    public required string SessionId { get; init; }
}

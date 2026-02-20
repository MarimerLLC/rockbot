namespace RockBot.UserProxy;

/// <summary>
/// Message sent from a human user to the agent bus.
/// </summary>
public sealed record UserMessage
{
    public required string Content { get; init; }
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public string? TargetAgent { get; init; }
}

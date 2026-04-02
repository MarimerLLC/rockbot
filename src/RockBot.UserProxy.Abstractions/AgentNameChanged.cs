namespace RockBot.UserProxy;

/// <summary>
/// Notification published to <see cref="UserProxyTopics.UserResponse"/> when the
/// agent's display name changes. All user proxy frontends should update their
/// displayed agent name in response.
/// </summary>
public sealed record AgentNameChanged
{
    /// <summary>
    /// The new display name, or null/empty if the name was cleared (revert to default).
    /// </summary>
    public required string AgentName { get; init; }
}

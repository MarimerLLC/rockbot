namespace RockBot.UserProxy;

/// <summary>
/// Agent identity metadata returned in response to <see cref="AgentInfoRequest"/>.
/// </summary>
public sealed record AgentInfoResponse
{
    public required string AgentName { get; init; }
    public required string AgentVersion { get; init; }
}

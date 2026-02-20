namespace RockBot.A2A;

/// <summary>
/// Request to cancel an in-progress agent task.
/// Published to "agent.task.cancel.{agentName}".
/// </summary>
public sealed record AgentTaskCancelRequest
{
    public required string TaskId { get; init; }
    public string? ContextId { get; init; }
}

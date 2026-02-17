namespace RockBot.A2A;

/// <summary>
/// Request to submit a task to another agent.
/// Published to "agent.task.{agentName}".
/// </summary>
public sealed record AgentTaskRequest
{
    public required string TaskId { get; init; }
    public string? ContextId { get; init; }
    public required string Skill { get; init; }
    public required AgentMessage Message { get; init; }
}

namespace RockBot.A2A;

/// <summary>
/// Intermediate status notification for an in-progress task.
/// Published to the status topic.
/// </summary>
public sealed record AgentTaskStatusUpdate
{
    public required string TaskId { get; init; }
    public string? ContextId { get; init; }
    public required AgentTaskState State { get; init; }
    public AgentMessage? Message { get; init; }
}

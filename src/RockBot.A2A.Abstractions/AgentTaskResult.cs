namespace RockBot.A2A;

/// <summary>
/// Final result of an agent-to-agent task.
/// Published to the ReplyTo topic.
/// </summary>
public sealed record AgentTaskResult
{
    public required string TaskId { get; init; }
    public string? ContextId { get; init; }
    public required AgentTaskState State { get; init; }
    public IReadOnlyList<AgentArtifact>? Artifacts { get; init; }
    public AgentMessage? Message { get; init; }
}

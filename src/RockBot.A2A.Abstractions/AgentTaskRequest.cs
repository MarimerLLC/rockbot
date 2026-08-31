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

    /// <summary>
    /// Optional structured metadata attached to the overall request, propagated
    /// from the A2A v1 <c>SendMessageRequest.metadata</c> field. Bridges carry
    /// this through the bus so capability handlers can read per-task
    /// configuration or identifiers alongside the message payload.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

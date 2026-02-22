namespace RockBot.A2A;

/// <summary>
/// Tracks an in-flight A2A task dispatched by the primary agent.
/// </summary>
internal sealed record PendingA2ATask
{
    public required string TaskId { get; init; }
    public required string TargetAgent { get; init; }
    public required string PrimarySessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required CancellationTokenSource Cts { get; init; }
}

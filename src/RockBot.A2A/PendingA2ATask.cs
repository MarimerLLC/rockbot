namespace RockBot.A2A;

/// <summary>
/// Tracks an in-flight A2A task dispatched by the primary agent.
/// Multi-turn state fields (<see cref="ContextId"/>, <see cref="InputRequiredRound"/>,
/// <see cref="LastInputRequiredQuestion"/>, <see cref="LastInputRequiredAnswer"/>)
/// are mutable because the record lives in <see cref="A2ATaskTracker"/> and is
/// updated across InputRequired follow-up rounds.
/// </summary>
internal sealed record PendingA2ATask
{
    public required string TaskId { get; init; }
    public required string TargetAgent { get; init; }
    public required string Skill { get; init; }
    public required string PrimarySessionId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required CancellationTokenSource Cts { get; init; }

    /// <summary>Context ID for multi-turn follow-up (set from first non-terminal response).</summary>
    public string? ContextId { get; set; }

    /// <summary>Number of InputRequired round-trips completed so far.</summary>
    public int InputRequiredRound { get; set; }

    /// <summary>Text of the last InputRequired question (for repetition detection).</summary>
    public string? LastInputRequiredQuestion { get; set; }

    /// <summary>Text of the last response sent back (for repetition detection).</summary>
    public string? LastInputRequiredAnswer { get; set; }
}

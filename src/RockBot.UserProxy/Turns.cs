namespace RockBot.UserProxy;

/// <summary>Options for <see cref="UserProxyService.SendTurnAsync"/>.</summary>
public sealed record TurnOptions
{
    /// <summary>
    /// The correlation id to send the turn with (it becomes the agent's run id). Lets a frontend
    /// use its own turn id. A fresh id is generated when null.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>Receives the turn's non-final replies (tool calls, status text).</summary>
    public IProgress<AgentReply>? Progress { get; init; }

    /// <summary>Receives the turn's structured events (run lifecycle, task list).</summary>
    public ITurnObserver? Observer { get; init; }

    /// <summary>
    /// How long to wait for the agent to start the run, or, for an agent too old to send run
    /// events, for its final reply. Defaults to <see cref="UserProxyOptions.DefaultReplyTimeout"/>.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Once the run has started, how long the agent may stay silent before the turn ends as
    /// <see cref="TurnResultOutcome.AgentLost"/>. Defaults to
    /// <see cref="UserProxyOptions.HeartbeatTimeout"/>, else three heartbeat intervals.
    /// </summary>
    public TimeSpan? HeartbeatTimeout { get; init; }

    /// <summary>
    /// The longest the turn may run, however often the agent reports in. Defaults to
    /// <see cref="UserProxyOptions.MaxTurnDuration"/>. A turn that hits it ends as
    /// <see cref="TurnResultOutcome.TimedOut"/>.
    /// </summary>
    public TimeSpan? MaxDuration { get; init; }
}

/// <summary>How a turn sent with <see cref="UserProxyService.SendTurnAsync"/> ended.</summary>
public enum TurnResultOutcome
{
    /// <summary>The agent's final reply arrived.</summary>
    Success,

    /// <summary>The agent reported the run cancelled.</summary>
    Cancelled,

    /// <summary>The agent reported the run failed. Any error reply it sent is attached.</summary>
    Error,

    /// <summary>The run started, then the agent went silent for longer than the heartbeat timeout.</summary>
    AgentLost,

    /// <summary>
    /// Nothing arrived in time, or the turn ran past its maximum duration. The agent may still be
    /// working (or may be too old to send run events).
    /// </summary>
    TimedOut,
}

/// <summary>The result of <see cref="UserProxyService.SendTurnAsync"/>.</summary>
public sealed record TurnResult
{
    public required TurnResultOutcome Outcome { get; init; }

    /// <summary>The agent's final reply, when one arrived.</summary>
    public AgentReply? Reply { get; init; }

    /// <summary>The agent's error text for <see cref="TurnResultOutcome.Error"/>, or why the turn ended otherwise.</summary>
    public string? Error { get; init; }

    public required string CorrelationId { get; init; }

    /// <summary>True when the agent sent a <see cref="RunStarted"/>, so the outcome came from run events.</summary>
    public bool RunStarted { get; init; }
}

/// <summary>
/// Receives the structured events of one turn sent with <see cref="UserProxyService.SendTurnAsync"/>.
/// Called on the message-handling thread; implementations should be quick and not throw.
/// </summary>
public interface ITurnObserver
{
    void OnRunStarted(RunStarted run) { }
    void OnRunHeartbeat(RunHeartbeat heartbeat) { }
    void OnRunFinished(RunFinished run) { }
    void OnTaskListSnapshot(TaskListSnapshot snapshot) { }
    void OnTaskListDelta(TaskListDelta delta) { }
}

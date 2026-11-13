namespace RockBot.UserProxy;

/// <summary>
/// The agent started working on a user turn. Published to the turn's <c>ReplyTo</c> topic with
/// the turn's correlation id, only when the message advertised
/// <see cref="ClientCapabilities.AgentEvents"/>. Modeled on AG-UI's <c>RUN_STARTED</c>.
/// </summary>
public sealed record RunStarted
{
    /// <summary>The raw session id the turn belongs to (AG-UI <c>threadId</c>).</summary>
    public required string ThreadId { get; init; }

    /// <summary>The turn's correlation id (AG-UI <c>runId</c>).</summary>
    public required string RunId { get; init; }

    public required string AgentName { get; init; }

    /// <summary>
    /// How often the agent sends a <see cref="RunHeartbeat"/> while the run is active. A proxy
    /// that hears nothing for a few intervals can conclude the agent was lost.
    /// </summary>
    public required int HeartbeatIntervalSeconds { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// The agent is still working on the run. Sent periodically between <see cref="RunStarted"/> and
/// <see cref="RunFinished"/>.
/// </summary>
public sealed record RunHeartbeat
{
    public required string ThreadId { get; init; }
    public required string RunId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required double ElapsedSeconds { get; init; }

    /// <summary>The latest tool or stage text, for a "thinking…" indicator. Null when nothing has happened yet.</summary>
    public string? Status { get; init; }
}

/// <summary>
/// The agent finished the run. A successful run's final <see cref="AgentReply"/> is published
/// before this event, on the same topic.
/// </summary>
public sealed record RunFinished
{
    public required string ThreadId { get; init; }
    public required string RunId { get; init; }

    /// <summary>One of <see cref="RunOutcome"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>What went wrong, for <see cref="RunOutcome.Error"/>.</summary>
    public string? Error { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>Outcomes a <see cref="RunFinished"/> reports. An AG-UI proxy maps <see cref="Error"/> to <c>RUN_ERROR</c>.</summary>
public static class RunOutcome
{
    public const string Success = "success";
    public const string Cancelled = "cancelled";
    public const string Error = "error";
}

namespace RockBot.UserProxy;

/// <summary>
/// Structured description of one tool call, carried on a progress <see cref="AgentReply.ToolCall"/>.
/// A call is reported twice under the same <see cref="ToolCallId"/>: once when it starts and once
/// when it finishes or fails. Modeled on AG-UI's <c>TOOL_CALL_*</c> events.
/// </summary>
public sealed record ToolCallInfo
{
    public required string ToolCallId { get; init; }
    public required string ToolCallName { get; init; }

    /// <summary>One of <see cref="ToolCallPhase"/>.</summary>
    public required string Phase { get; init; }

    /// <summary>A short, human-readable summary of the arguments.</summary>
    public string? ArgsSummary { get; init; }

    /// <summary>A short summary of the result, for <see cref="ToolCallPhase.Finished"/>.</summary>
    public string? Result { get; init; }

    /// <summary>How long the call took, for <see cref="ToolCallPhase.Finished"/> and <see cref="ToolCallPhase.Failed"/>.</summary>
    public long? DurationMs { get; init; }

    /// <summary>What went wrong, for <see cref="ToolCallPhase.Failed"/>.</summary>
    public string? Error { get; init; }
}

/// <summary>Phases a <see cref="ToolCallInfo"/> reports.</summary>
public static class ToolCallPhase
{
    public const string Started = "started";
    public const string Finished = "finished";
    public const string Failed = "failed";
}

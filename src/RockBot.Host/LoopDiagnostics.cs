namespace RockBot.Host;

/// <summary>
/// Mutable diagnostic state populated by <see cref="AgentLoopRunner.RunAsync"/> and
/// <see cref="RockBotFunctionInvokingChatClient"/> as the LLM tool loop progresses.
/// Callers create an instance and pass it in; even when the loop throws or is
/// cancelled, the partially-populated snapshot is available in the caller's
/// catch block so failure-detail handlers (notably <c>SubagentRunner</c>) can
/// report what the agent was doing at the moment of failure.
/// </summary>
public sealed class LoopDiagnostics
{
    /// <summary>Number of LLM iterations the loop completed (text-based path) or invoked (native path).</summary>
    public int Iterations { get; set; }

    /// <summary>Total tool calls observed across all iterations.</summary>
    public int ToolCalls { get; set; }

    /// <summary>The last non-empty assistant text the loop produced, if any.</summary>
    public string? LastAssistantText { get; set; }

    /// <summary>Name of the most recently invoked tool (whether it succeeded, failed, or was in flight).</summary>
    public string? LastToolName { get; set; }

    /// <summary>Argument summary of the most recently invoked tool (compact, may be truncated).</summary>
    public string? LastToolArguments { get; set; }

    /// <summary>Result preview of the most recently invoked tool, or null if the tool was still running when the loop exited.</summary>
    public string? LastToolResult { get; set; }

    /// <summary>Status of the most recently invoked tool ("ok", "error", "timeout", or "in-flight").</summary>
    public string? LastToolStatus { get; set; }

    /// <summary>When the most recently invoked tool started.</summary>
    public DateTimeOffset? LastToolStartedAt { get; set; }

    /// <summary>When the most recently invoked tool completed; null if it never returned.</summary>
    public DateTimeOffset? LastToolCompletedAt { get; set; }

    // ── Token usage (populated by AgentLoopRunner after the loop completes) ──

    /// <summary>Total input tokens consumed across all loop iterations.</summary>
    public long InputTokens { get; set; }

    /// <summary>Total output tokens produced across all loop iterations.</summary>
    public long OutputTokens { get; set; }

    /// <summary>Model ID reported by the LLM for this loop run (last seen value when multiple responses occur).</summary>
    public string? ModelId { get; set; }
}

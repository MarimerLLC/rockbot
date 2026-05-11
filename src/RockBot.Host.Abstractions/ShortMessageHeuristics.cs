namespace RockBot.Host;

/// <summary>
/// Shared thresholds for treating an incoming user message as a low-signal
/// follow-up. Below the threshold, BM25-style topic searches over the raw
/// message text return noise that drowns out the recent conversation thread,
/// leading the LLM to summarise injected memory instead of replying to what
/// was actually said. Consumers include <see cref="AgentContextBuilder"/>'s
/// per-turn search gate, the tier selector's active-thread override, and the
/// AgentLoopRunner memory-summary-reply guard. See issue #383.
/// </summary>
public static class ShortMessageHeuristics
{
    /// <summary>
    /// Maximum character length, inclusive, for a user message to be treated as
    /// a short follow-up. Picked empirically from production incidents in
    /// blazor-session — the 18-char "I'll find out soon" sat well inside this
    /// band, and the longer 67-char variant is handled as a separate concern.
    /// </summary>
    public const int UserMessageCharThreshold = 30;
}

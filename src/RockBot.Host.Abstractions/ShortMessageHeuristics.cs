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

    /// <summary>
    /// Maximum character length, inclusive, for a user message to be treated as a
    /// conversational follow-up for the purposes of the memory-narration guard.
    /// Deliberately wider than <see cref="UserMessageCharThreshold"/>: a fact-introducing
    /// follow-up ("Hopefully we can go this coming winter. My health seems better now",
    /// 66 chars) sits above the lexical-noise band that gates the BM25 and routing
    /// defenses, but still exhibits the storing-and-summarising failure. Only consumed
    /// by the AgentLoopRunner guard, which additionally requires a memory write and a
    /// narration-shaped response — the 30-char gates are unaffected. See issue #397.
    /// </summary>
    public const int FollowUpMessageCharThreshold = 120;

    /// <summary>
    /// Minimum number of prior turns before a session counts as an established
    /// conversational thread. Conservative enough to leave first-turn messages on the
    /// unmodified path.
    /// </summary>
    public const int ThreadEstablishedMinTurns = 3;

    /// <summary>
    /// Maximum age of the most recent prior turn for a thread to still count as active.
    /// </summary>
    public static readonly TimeSpan ThreadEstablishedRecency = TimeSpan.FromMinutes(30);
}

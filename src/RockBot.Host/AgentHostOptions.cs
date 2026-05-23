namespace RockBot.Host;

/// <summary>
/// A topic subscription paired with its dispatch concurrency.
/// </summary>
/// <param name="Topic">The topic pattern (with wildcards).</param>
/// <param name="DispatchConcurrency">Maximum concurrent in-flight handler invocations
/// for this subscription. Default 1 (sequential, preserves ordering). Bump only for
/// re-entrant handlers where cross-message coordination would otherwise deadlock the
/// consumer (e.g. the subagent-result consolidation gate).</param>
public sealed record TopicSubscription(string Topic, int DispatchConcurrency = 1);

/// <summary>
/// Configuration options for the agent host.
/// </summary>
public sealed class AgentHostOptions
{
    /// <summary>
    /// Topics the agent subscribes to, paired with their dispatch concurrency.
    /// </summary>
    public List<TopicSubscription> Topics { get; } = [];

    /// <summary>
    /// Default maximum number of tool-calling round-trips per request.
    /// Individual models may override this via <c>ModelBehavior.MaxToolIterationsOverride</c>.
    /// Defaults to 50.
    /// </summary>
    public int MaxToolIterations { get; set; } = 50;

    /// <summary>
    /// Maximum number of times the completion evaluator can re-prompt the agent when it
    /// determines the task is incomplete. Set to 0 to disable completion evaluation entirely.
    /// Individual models may override this via <c>ModelBehavior.MaxCompletionRepromptsOverride</c>.
    /// Defaults to 1.
    /// </summary>
    public int MaxCompletionReprompts { get; set; } = 1;

    /// <summary>
    /// Maximum number of proactive follow-up passes the agent can take after completing
    /// the user's request. A follow-up pass lets the agent take additional helpful actions
    /// within the context of the conversation (e.g. looking up a contact, cross-referencing
    /// calendar events) without being explicitly asked. Set to 0 to disable.
    /// Individual models may override this via <c>ModelBehavior.MaxFollowUpPassesOverride</c>.
    /// Defaults to 1.
    /// </summary>
    public int MaxFollowUpPasses { get; set; } = 1;

    /// <summary>
    /// Maximum time to wait for a single LLM API call before aborting and treating
    /// it as a failure. Applies to all tiers. The evaluators (completion, follow-up)
    /// will fail-open on timeout; tool loops will propagate the error.
    /// Set to <see cref="TimeSpan.Zero"/> to disable (rely on HTTP-level NetworkTimeout only).
    /// Defaults to 90 seconds.
    /// </summary>
    public TimeSpan LlmCallTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long an overflow-trimmed tool result remains available in working memory
    /// for retrieval via <c>GetFromWorkingMemory</c>. Defaults to 60 minutes — long
    /// enough that the stash outlives a single agent run including completion
    /// re-prompts and follow-up passes.
    /// </summary>
    public int ToolResultStashTtlMinutes { get; set; } = 60;

    /// <summary>
    /// Fraction of the surviving surface that goes to the head of the trimmed tool
    /// result (the tail gets the remainder). Default 0.6 — favors the head slightly
    /// because tools often lead with structured metadata, but keeps a meaningful tail
    /// for the final log lines / closing JSON / row counts that the old head-only
    /// trimmer used to discard. Clamped to [0.0, 1.0] at use time.
    /// </summary>
    public double ToolResultStashHeadTailRatio { get; set; } = 0.6;

    /// <summary>
    /// Soft context-size watermark in tokens. When the running message list exceeds
    /// this size before an LLM call, large tool results are trimmed into the WM stash
    /// proactively — without waiting for a provider-side context-overflow error.
    /// Default 30,000 tokens (≈108,000 chars at the 4-chars-per-token estimate, which
    /// trims to a ~27k-token effective ceiling because the trimmer targets 90% of
    /// the char budget). The per-tool-result cap (<see cref="ToolResultMaxChars"/>)
    /// already prevents any single tool from singlehandedly bloating the loop, so the
    /// watermark mostly catches cumulative bloat across many medium-sized results;
    /// dropping it to 25k forced hyper-elision of recent results in live runs.
    /// Set to 0 to disable proactive trimming and fall back to the legacy behaviour
    /// (trim only after a 400 overflow has been observed).
    /// </summary>
    public int ToolResultStashWatermarkTokens { get; set; } = 30_000;

    /// <summary>
    /// Per-tool-result hard cap in characters. Any single tool result longer than this
    /// is immediately stashed in working memory and replaced in-context with a
    /// head + elision marker + tail surface (same mechanism as the watermark trimmer,
    /// applied per-call instead of per-context). This catches the common case where one
    /// tool — typically an MCP schema dump or a long search result — singlehandedly
    /// bloats a subagent run without crossing the global watermark.
    /// Default 8,000 chars (≈2,000 tokens). Set to 0 to disable per-call capping and
    /// rely solely on the watermark.
    /// </summary>
    public int ToolResultMaxChars { get; set; } = 8_000;

    /// <summary>
    /// How many tool-call iterations a BM25-recalled skill body stays in context
    /// without being referenced (via a follow-up <c>get_skill</c>) before it's unloaded.
    /// Bodies are ~3,000 chars each and remain visible to the model for the entire inner
    /// loop even when no longer relevant; unloading them after this many idle iterations
    /// keeps the loop lean. Subagent character is unaffected — the model can re-fetch
    /// the body at any time by calling <c>get_skill</c> again.
    /// Default 5. Set to 0 to disable aging and leave all skill bodies in context.
    /// </summary>
    public int SkillBodyUnloadAfterIterations { get; set; } = 5;
}

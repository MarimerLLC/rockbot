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
}

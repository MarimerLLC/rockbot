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
    /// Whether to inject the reasoning-scaffolding system message (iteration budget plus
    /// "think through what steps are needed to fully complete the request") ahead of the
    /// final user message, and to attach the task-list tools that go with it.
    /// <para>
    /// Defaults to <c>true</c>, which suits task-completing agents. Set to <c>false</c> for
    /// conversational or creative agents — companions, storytelling, interactive fiction — where
    /// framing a reply as a task to be "fully completed" pushes the model toward
    /// summarising and wrapping up rather than staying in the moment.
    /// </para>
    /// <para>
    /// Callers that pass an explicit value to <c>AgentLoopRunner.RunAsync</c> still win;
    /// this only supplies the default. Workers, for example, always opt out.
    /// </para>
    /// </summary>
    public bool EnableReasoningScaffolding { get; set; } = true;

    /// <summary>
    /// Whether skills participate in prompt assembly: the once-per-session skill index, the
    /// per-turn BM25 skill recall, and the startup seeding of a starter skill per registered
    /// tool-skill provider. Defaults to <c>true</c>.
    /// <para>
    /// Set to <c>false</c> for conversational or creative agents. Skill bodies describe tool
    /// workflows — subagents, wisps, scheduling, service search — and injecting several
    /// thousand tokens of them ahead of every turn buries the agent's persona and reframes
    /// the conversation as a task to be executed with tools. Deleting the skill files is not
    /// sufficient on its own, because the starter-skill seeder recreates them on each
    /// startup; this flag turns off both the seeding and the injection.
    /// </para>
    /// </summary>
    public bool EnableSkillInjection { get; set; } = true;

    /// <summary>
    /// Whether the per-turn service-search hint block (candidate A2A agents and MCP servers
    /// matched against the user's message) is injected. Defaults to <c>true</c>.
    /// <para>
    /// Set to <c>false</c> for conversational or creative agents, where a system message
    /// listing available services and telling the model to call <c>search_known_services</c>
    /// reliably surfaces as an out-of-character menu of capabilities.
    /// </para>
    /// </summary>
    public bool EnableServiceHints { get; set; } = true;

    /// <summary>
    /// Sampling temperature applied to every agent LLM call. Null (the default) leaves the
    /// provider default in place. Raise it for creative work, lower it for deterministic
    /// task execution.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// Penalty applied to tokens in proportion to how often they have already appeared,
    /// discouraging verbatim repetition. Null (the default) leaves the provider default —
    /// which for most OpenAI-compatible endpoints is 0, i.e. no penalty at all.
    /// <para>
    /// Worth setting for conversational and creative agents. With no penalty a model that
    /// lands on a serviceable closing line will reuse it every turn, and once the phrase is
    /// in the history it reinforces itself. Typical useful range is 0.3–0.8; values above
    /// ~1.0 start to distort vocabulary.
    /// </para>
    /// </summary>
    public float? FrequencyPenalty { get; set; }

    /// <summary>
    /// Penalty applied to tokens that have appeared at all, regardless of count, pushing the
    /// model toward introducing new subject matter. Null (the default) leaves the provider
    /// default. Useful alongside <see cref="FrequencyPenalty"/> for agents that should keep
    /// moving to new material rather than restating the current situation.
    /// </summary>
    public float? PresencePenalty { get; set; }

    /// <summary>
    /// Multiplicative penalty applied to the logit of every token that has already appeared
    /// anywhere in the context. Null (the default) omits the field entirely, leaving provider
    /// behaviour unchanged. 1.0 is the neutral value; useful settings sit around 1.05–1.15.
    /// <para>
    /// This is NOT interchangeable with <see cref="FrequencyPenalty"/>, and it is the only
    /// one of the two that stops a model replaying a whole previous reply word for word.
    /// Frequency penalty is additive and scales with a token's count, so across a long
    /// verbatim copy it barely shifts the distribution — measured against a real looped
    /// conversation, values of 0.5, 1.0 and 1.5 all reproduced the previous reply exactly,
    /// while a repetition penalty of 1.1 broke the loop immediately. Character-voice and other
    /// long-form conversational finetunes are especially prone to this: once an identical
    /// reply sits in the history two or three times it dominates the probability mass, and
    /// temperature alone cannot escape it.
    /// </para>
    /// <para>
    /// Sent as the non-standard <c>repetition_penalty</c> body field, which
    /// <c>Microsoft.Extensions.AI</c>'s <c>ChatOptions</c> has no slot for, so it is injected
    /// by a client pipeline policy rather than through <c>ChatOptions</c>. Endpoints that do
    /// not recognise the field generally ignore it. Above roughly 1.2 output degrades into
    /// incoherence, so raise it in small steps.
    /// </para>
    /// </summary>
    public float? RepetitionPenalty { get; set; }

    /// <summary>
    /// Cap on tokens generated per reply. Null (the default) leaves the provider default,
    /// which on some hosts is low enough to truncate long replies mid-sentence. Raise it for
    /// agents that are expected to produce long-form prose.
    /// </summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// Whether a failed turn's error text ("Sorry, I encountered an error: ...") is recorded
    /// in conversation memory as an assistant turn. Defaults to <c>true</c>, preserving the
    /// existing behaviour of keeping the transcript faithful to what the user saw.
    /// <para>
    /// Set to <c>false</c> for conversational or creative agents. A transient transport
    /// failure is not something the agent said, and persisting it puts HTTP status text into
    /// the model's own context on every later turn — which degrades continuity and, for
    /// smaller models, can leave verbatim repetition of an earlier reply as the
    /// highest-probability continuation. The user still sees the error; it just is not
    /// written into history.
    /// </para>
    /// </summary>
    public bool PersistErrorTurns { get; set; } = true;

    /// <summary>
    /// Number of the most recent conversation turns replayed into the LLM context on each
    /// call. Defaults to 20.
    /// <para>
    /// Raise this for agents on large-context models where continuity matters more than
    /// prompt cost — a creative or long-running conversational agent loses the thread when
    /// the window is short. Values above
    /// <see cref="ConversationMemoryOptions.MaxTurnsPerSession"/> have no effect, because
    /// retention caps how many turns exist to replay.
    /// </para>
    /// </summary>
    public int MaxLlmContextTurns { get; set; } = 20;

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

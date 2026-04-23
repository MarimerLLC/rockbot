namespace RockBot.Llm;

/// <summary>
/// Describes model-specific behavioral tweaks applied by the agent runtime.
/// Resolved from <see cref="IModelBehaviorProvider"/> at startup for the configured model
/// and registered in DI as a singleton so consumers can inject it directly.
/// </summary>
public sealed class ModelBehavior
{
    /// <summary>
    /// Character count above which a tool result is chunked into working memory
    /// rather than appended inline to the chat history. Operators can raise this
    /// for models with large context windows or lower it for small-context models.
    /// Defaults to 64 000 characters (~16 000 tokens), appropriate for models with
    /// 120k+ token context windows.
    /// </summary>
    public int ToolResultChunkingThreshold { get; init; } = 64_000;

    /// <summary>Behavior profile that applies no tweaks — used when no overrides are configured.</summary>
    public static readonly ModelBehavior Default = new()
    {
        NudgeOnHallucinatedToolCalls = true,
        NudgeOnToolFailureGiveup = true,
    };

    /// <summary>
    /// When true, detect responses where the model claims to have called tools (e.g. "I've
    /// scheduled", "I've cancelled") without emitting any actual function calls, and inject a
    /// nudge message that forces real tool execution on the next iteration.
    /// Addresses hallucination common in DeepSeek and similar models.
    /// </summary>
    public bool NudgeOnHallucinatedToolCalls { get; init; }

    /// <summary>
    /// Optional extra content appended as a system message on every LLM call, after the
    /// agent profile and rules but before conversation history. Use this to inject
    /// model-specific guardrails — e.g. reminders not to fabricate tool names for models
    /// that are prone to content hallucination. Null means nothing extra is injected.
    /// </summary>
    public string? AdditionalSystemPrompt { get; init; }

    /// <summary>
    /// Optional prompt injected as a system message at the start of each tool-calling
    /// iteration, visible only while the tool loop is active. Use this to reinforce
    /// model-specific constraints during agentic execution without polluting the
    /// initial system prompt. Null means nothing extra is injected.
    /// </summary>
    public string? PreToolLoopPrompt { get; init; }

    /// <summary>
    /// Overrides the default maximum number of tool-calling round-trips per request.
    /// Useful when a model is slower to converge (needs more iterations) or faster
    /// to drift (needs a tighter cap). Null means use the handler's built-in default.
    /// </summary>
    public int? MaxToolIterationsOverride { get; init; }

    /// <summary>
    /// When true, inject a confirmation step before executing tools that are marked
    /// as destructive (e.g. delete, send, cancel). The model is asked to restate
    /// what it is about to do and why before the call proceeds.
    /// Not yet implemented — reserved for future use.
    /// </summary>
    public bool RequireExplicitConfirmationForDestructiveTools { get; init; }

    /// <summary>
    /// When true, the agent uses text-based tool-call parsing (the manual loop that
    /// detects <c>tool_call_name:</c> patterns in free text) instead of M.E.AI's
    /// <c>FunctionInvokingChatClient</c> middleware. This is required for models that
    /// do not support native structured tool calling (e.g. DeepSeek).
    /// Default is <c>false</c> — most models support native structured tool calling.
    /// </summary>
    public bool UseTextBasedToolCalling { get; init; }

    /// <summary>
    /// Controls how the model presents results at the end of a scheduled-task run.
    /// Defaults to <see cref="ScheduledTaskResultMode.Summarize"/> (current behaviour).
    /// Set to <see cref="ScheduledTaskResultMode.VerbatimOutput"/> for models that tend
    /// to paraphrase rather than show actual output, or
    /// <see cref="ScheduledTaskResultMode.SummarizeWithOutput"/> for both.
    /// </summary>
    public ScheduledTaskResultMode ScheduledTaskResultMode { get; init; } = ScheduledTaskResultMode.Summarize;

    /// <summary>
    /// Overrides the default maximum number of completion-evaluator re-prompts per request.
    /// Null means use the host's built-in default (<see cref="Host.AgentHostOptions.MaxCompletionReprompts"/>).
    /// </summary>
    public int? MaxCompletionRepromptsOverride { get; init; }

    /// <summary>
    /// Overrides the default maximum number of proactive follow-up passes per request.
    /// Null means use the host's built-in default (<see cref="Host.AgentHostOptions.MaxFollowUpPasses"/>).
    /// </summary>
    public int? MaxFollowUpPassesOverride { get; init; }

    /// <summary>
    /// When true, detect leaked internal tool-call scaffolding in the model's text output
    /// (e.g. <c>to=multi_tool_use.parallel</c> or <c>to=functions.X</c>) and force a retry
    /// (consuming one completion-reprompt slot) instead of returning the malformed response.
    /// Targets a specific, documented failure mode of OpenAI GPT-family models where the
    /// model emits training-time scaffolding as literal text. Language-agnostic and safe
    /// to enable for any deployment; legitimate responses never contain these tokens.
    /// </summary>
    public bool NudgeOnLeakedToolSyntax { get; init; }

    /// <summary>
    /// When true, detect runs of 3+ consecutive CJK codepoints in the model's output and
    /// force a retry. Intended for English-primary deployments where CJK output correlates
    /// with the model producing gambling-SEO spam or other training-data contamination.
    /// This is a heuristic — DO NOT enable for agents that legitimately process or respond
    /// in Chinese or Japanese, or it will force unnecessary retries on valid responses.
    /// </summary>
    public bool NudgeOnUnexpectedCjkOutput { get; init; }

    /// <summary>
    /// When true, detect responses where the model gives up after a tool returned an
    /// error ("I hit a tool failure", "errored on both", "from the current tool state",
    /// etc.) and inject a nudge telling it to retry the tool once before reporting failure.
    /// The <see cref="Host.AgentLoopRunner.RepetitiveToolCallDetector"/> and the existing
    /// reprompt budget bound total retries, so persistent failures still surface to the user.
    /// Enabled by default — general-purpose and model-agnostic.
    /// </summary>
    public bool NudgeOnToolFailureGiveup { get; init; }
}

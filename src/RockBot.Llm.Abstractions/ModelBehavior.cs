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
    /// Defaults to 16 000 characters (~4 000 tokens).
    /// </summary>
    public int ToolResultChunkingThreshold { get; init; } = 16_000;

    /// <summary>Behavior profile that applies no tweaks — used when no overrides are configured.</summary>
    public static readonly ModelBehavior Default = new() { NudgeOnHallucinatedToolCalls = true };

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
    /// Controls how the model presents results at the end of a scheduled-task run.
    /// Defaults to <see cref="ScheduledTaskResultMode.Summarize"/> (current behaviour).
    /// Set to <see cref="ScheduledTaskResultMode.VerbatimOutput"/> for models that tend
    /// to paraphrase rather than show actual output, or
    /// <see cref="ScheduledTaskResultMode.SummarizeWithOutput"/> for both.
    /// </summary>
    public ScheduledTaskResultMode ScheduledTaskResultMode { get; init; } = ScheduledTaskResultMode.Summarize;
}

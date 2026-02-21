namespace RockBot.Llm;

/// <summary>
/// Configuration for per-model behavioral tweaks, bound from the "ModelBehaviors" config section.
/// </summary>
public sealed class ModelBehaviorOptions
{
    /// <summary>
    /// Base directory for per-model prompt files. Each model family gets a subdirectory
    /// named by its prefix (e.g. "deepseek"), containing markdown files for each prompt
    /// property (e.g. "additional-system-prompt.md", "pre-tool-loop-prompt.md").
    /// Relative paths are resolved against <see cref="AppContext.BaseDirectory"/>.
    /// Defaults to <c>"model-behaviors"</c>.
    /// </summary>
    public string BasePath { get; set; } = "model-behaviors";

    /// <summary>
    /// Per-model behavior overrides keyed by model ID or prefix.
    /// Prefix matching is supported: "deepseek" matches "deepseek/deepseek-v3.1-terminus".
    /// Exact matches take priority; among prefix matches, longer prefixes win.
    /// File-based prompt values (loaded from <see cref="BasePath"/>) take priority over
    /// inline values set here.
    /// </summary>
    public Dictionary<string, ModelBehaviorEntry> Models { get; set; } = [];
}

/// <summary>Behavior overrides for a single model or model family.</summary>
public sealed class ModelBehaviorEntry
{
    /// <inheritdoc cref="ModelBehavior.NudgeOnHallucinatedToolCalls"/>
    public bool NudgeOnHallucinatedToolCalls { get; set; }

    /// <inheritdoc cref="ModelBehavior.AdditionalSystemPrompt"/>
    public string? AdditionalSystemPrompt { get; set; }

    /// <inheritdoc cref="ModelBehavior.PreToolLoopPrompt"/>
    public string? PreToolLoopPrompt { get; set; }

    /// <inheritdoc cref="ModelBehavior.MaxToolIterationsOverride"/>
    public int? MaxToolIterationsOverride { get; set; }

    /// <inheritdoc cref="ModelBehavior.RequireExplicitConfirmationForDestructiveTools"/>
    public bool RequireExplicitConfirmationForDestructiveTools { get; set; }

    /// <inheritdoc cref="ModelBehavior.ScheduledTaskResultMode"/>
    public ScheduledTaskResultMode ScheduledTaskResultMode { get; set; } = ScheduledTaskResultMode.Summarize;

    /// <inheritdoc cref="ModelBehavior.ToolResultChunkingThreshold"/>
    public int? ToolResultChunkingThreshold { get; set; }
}

using System.Text.Json.Serialization;

namespace RockBot.Subagent.Worker;

/// <summary>
/// Input to a single worker spawn. Workers are leaf gather tasks — see
/// <c>design/worker-subagents.md</c> for the full contract.
/// </summary>
public sealed record WorkerDefinition
{
    /// <summary>
    /// One-sentence imperative describing what the worker should do. Required.
    /// </summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>
    /// Optional pre-resolved handoff from the spawning agent (active accounts,
    /// known IDs, etc.). The worker treats this as ground truth and does not
    /// re-investigate facts already supplied here.
    /// </summary>
    [JsonPropertyName("context")]
    public string? Context { get; init; }

    /// <summary>
    /// Optional override for the working-memory key the worker writes its
    /// structured output to. When null, defaults to <c>worker/&lt;task-id&gt;/result</c>.
    /// </summary>
    [JsonPropertyName("result_key")]
    public string? ResultKey { get; init; }

    /// <summary>
    /// Soft wall-clock cap in minutes. When null, falls back to
    /// <see cref="WorkerOptions.DefaultTimeoutMinutes"/>.
    /// </summary>
    [JsonPropertyName("timeout_minutes")]
    public int? TimeoutMinutes { get; init; }

    /// <summary>
    /// Optional allowlist of tool names (exact match) or name prefixes (trailing
    /// asterisk, e.g. <c>calendar-mcp.*</c>). When non-empty, only registry tools
    /// matching the list are exposed to the worker. Applied on top of the
    /// always-exclusions enforced by the worker runner.
    /// </summary>
    [JsonPropertyName("tools_allow")]
    public IReadOnlyList<string>? ToolsAllow { get; init; }
}

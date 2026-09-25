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
    /// Use <see cref="ResolveResultKey"/> for the full path every party reads and writes.
    /// </summary>
    [JsonPropertyName("result_key")]
    public string? ResultKey { get; init; }

    /// <summary>
    /// Resolves <see cref="ResultKey"/> to the absolute working-memory path shared by the
    /// worker (which saves there), <c>spawn_workers</c> (which inlines it), and the spawning
    /// agent (which may fetch it). A bare key such as <c>email-sweep</c> would otherwise mean
    /// three different paths — <c>worker/&lt;id&gt;/email-sweep</c> to the worker's namespaced
    /// save, the raw key to the executor's read, and <c>subagent/&lt;id&gt;/email-sweep</c> to
    /// the spawner's get — so the findings were saved but never found. Keys containing '/'
    /// are already absolute (same rule as <c>WorkingMemoryTools</c>) and pass through.
    /// </summary>
    public string ResolveResultKey(string taskId)
    {
        var key = ResultKey?.Trim();
        if (string.IsNullOrEmpty(key))
            return $"worker/{taskId}/result";
        return key.Contains('/') ? key : $"worker/{taskId}/{key}";
    }

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

using System.Text.Json.Serialization;

namespace RockBot.Subagent.Worker;

/// <summary>
/// Typed return of a single worker run. <see cref="ResultKey"/> points at the
/// working-memory entry where the worker stored its actual findings — the
/// spawning agent reads that key to consume the data, never the
/// <see cref="WorkerResult"/> alone.
/// </summary>
public sealed record WorkerResult
{
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("is_success")]
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Working-memory key the worker wrote its findings to. Auto-assigned to
    /// <c>worker/&lt;task-id&gt;/result</c> when the caller did not override it.
    /// </summary>
    [JsonPropertyName("result_key")]
    public required string ResultKey { get; init; }

    /// <summary>
    /// Count of distinct facts the worker reported recording. Self-reported by
    /// the worker; treated as a hint, not a strict invariant.
    /// </summary>
    [JsonPropertyName("facts_recorded")]
    public int FactsRecorded { get; init; }

    /// <summary>Items the worker could not verify and is handing back.</summary>
    [JsonPropertyName("blocked")]
    public IReadOnlyList<string> Blocked { get; init; } = [];

    /// <summary>
    /// Tool-call patterns the worker observed converging on success. The
    /// spawning agent reviews these on synthesis and may promote any worth
    /// keeping via <c>promote_skill_asset</c>.
    /// </summary>
    [JsonPropertyName("converged_patterns")]
    public IReadOnlyList<ConvergedPattern> ConvergedPatterns { get; init; } = [];

    [JsonPropertyName("duration")]
    public TimeSpan Duration { get; init; }

    [JsonPropertyName("llm_turns")]
    public int LlmTurns { get; init; }

    /// <summary>Populated only when <see cref="IsSuccess"/> is false.</summary>
    [JsonPropertyName("failure_reason")]
    public string? FailureReason { get; init; }
}

using System.Text.Json.Serialization;

namespace RockBot.Subagent.Worker;

/// <summary>
/// Result of executing a batch of workers (one or more) via <c>spawn_workers</c>.
/// </summary>
public sealed record WorkerBatchResult
{
    [JsonPropertyName("batch_id")]
    public required string BatchId { get; init; }

    [JsonPropertyName("results")]
    public required IReadOnlyList<WorkerResult> Results { get; init; }

    [JsonPropertyName("total_duration")]
    public required TimeSpan TotalDuration { get; init; }

    [JsonIgnore]
    public int TotalCount => Results.Count;

    [JsonIgnore]
    public int SucceededCount => Results.Count(r => r.IsSuccess);

    [JsonIgnore]
    public int FailedCount => Results.Count(r => !r.IsSuccess);
}

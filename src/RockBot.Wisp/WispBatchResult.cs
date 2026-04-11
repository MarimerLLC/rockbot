namespace RockBot.Wisp;

/// <summary>
/// Result of executing a batch of wisps (one or more) via spawn_wisps.
/// </summary>
public sealed record WispBatchResult
{
    /// <summary>Unique identifier for this batch execution.</summary>
    public required string BatchId { get; init; }

    /// <summary>Per-wisp execution results in submission order.</summary>
    public required IReadOnlyList<WispExecutionResult> Results { get; init; }

    /// <summary>Wall-clock duration from batch start to last wisp completion.</summary>
    public required TimeSpan TotalDuration { get; init; }

    public int TotalCount => Results.Count;
    public int SucceededCount => Results.Count(r => r.IsSuccess);
    public int FailedCount => Results.Count(r => !r.IsSuccess);
    public bool AllSucceeded => Results.All(r => r.IsSuccess);
}

namespace RockBot.Wisp;

/// <summary>
/// Classification of wisp step failures.
/// </summary>
public enum FailureCategory
{
    /// <summary>
    /// Wrong tool name, wrong gateway, missing params, bad step ordering.
    /// Learnable — skill bug.
    /// </summary>
    Structural,

    /// <summary>
    /// Service unavailable, network timeout, rate limited, auth expired.
    /// Not learnable — transient.
    /// </summary>
    External,

    /// <summary>
    /// LLM step picked wrong result, summarized poorly, missed key data.
    /// Partially learnable — prompt quality.
    /// </summary>
    Judgment,

    /// <summary>
    /// Unexpected file format, empty results, schema mismatch between steps.
    /// Learnable — assumption bug.
    /// </summary>
    Data
}

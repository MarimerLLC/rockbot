namespace RockBot.Host;

/// <summary>
/// Classifies a tool-call failure for downstream analysis by the dream system.
/// Shares the taxonomy defined in the wisp failure tracking design.
/// </summary>
public enum ToolCallFailureCategory
{
    /// <summary>No failure (successful call).</summary>
    None = 0,

    /// <summary>
    /// Wrong tool name, wrong parameter names, invalid parameter format, or tool name
    /// corruption (e.g. <c>search_files{}</c>). These are learnable — the dream system
    /// can feed corrections back into skills and directives.
    /// </summary>
    Structural,

    /// <summary>
    /// Service unavailable, network timeout, rate limited, or auth expired. Transient
    /// failures that should be retried or reported but not penalised.
    /// </summary>
    External,

    /// <summary>
    /// The same tool call was repeated 3+ times with identical arguments and results.
    /// Learnable — the dream system tracks thrashing frequency per tool/task combination.
    /// </summary>
    Thrashing,

    /// <summary>
    /// Path confusion (e.g. OneDrive path used on shared volume), wrong server for a
    /// resource. Learnable — corrections are fed into skills with additional context.
    /// </summary>
    Data
}

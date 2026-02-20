namespace RockBot.Scripts;

/// <summary>
/// Result of a script invocation. Published to the ReplyTo topic.
/// </summary>
public sealed record ScriptInvokeResponse
{
    /// <summary>
    /// LLM tool call ID for correlation.
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>
    /// Standard output from the script.
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// Standard error output from the script.
    /// </summary>
    public string? Stderr { get; init; }

    /// <summary>
    /// Process exit code.
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// Whether the script executed successfully (exit code 0).
    /// </summary>
    public bool IsSuccess => ExitCode == 0;

    /// <summary>
    /// Wall-clock execution time in milliseconds.
    /// </summary>
    public long ElapsedMs { get; init; }
}

namespace RockBot.Scripts;

/// <summary>
/// Request to invoke a script in a container. Published to "script.invoke".
/// </summary>
public sealed record ScriptInvokeRequest
{
    /// <summary>
    /// LLM tool call ID for correlation.
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>
    /// The script content to execute.
    /// </summary>
    public required string Script { get; init; }

    /// <summary>
    /// Optional input data to pass to the script via stdin.
    /// </summary>
    public string? InputData { get; init; }

    /// <summary>
    /// Maximum execution time in seconds. Defaults to 30.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Optional pip packages to install before running the script.
    /// </summary>
    public IReadOnlyList<string>? PipPackages { get; init; }
}

namespace RockBot.Tools;

/// <summary>
/// Error from a tool invocation. Published to the ReplyTo topic on failure.
/// </summary>
public sealed record ToolError
{
    /// <summary>
    /// LLM tool call ID for correlation.
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>
    /// Name of the tool that failed.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Error classification code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Whether the caller should consider retrying.
    /// </summary>
    public bool IsRetryable { get; init; }

    /// <summary>Well-known error codes.</summary>
    public static class Codes
    {
        public const string ToolNotFound = "tool_not_found";
        public const string ExecutionFailed = "execution_failed";
        public const string Timeout = "timeout";
        public const string InvalidArguments = "invalid_arguments";
    }
}

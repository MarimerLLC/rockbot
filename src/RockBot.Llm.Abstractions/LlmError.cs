namespace RockBot.Llm;

/// <summary>
/// Error from an LLM invocation. Published to the reply topic on provider failure.
/// </summary>
public sealed record LlmError
{
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
        public const string RateLimited = "rate_limited";
        public const string ProviderError = "provider_error";
        public const string Timeout = "timeout";
        public const string ContextTooLong = "context_too_long";
        public const string Unknown = "unknown";
    }
}

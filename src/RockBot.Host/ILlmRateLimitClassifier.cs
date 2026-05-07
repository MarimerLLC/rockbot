namespace RockBot.Host;

/// <summary>
/// Classifies exceptions thrown by underlying LLM SDK calls to determine whether
/// they represent a rate-limit (HTTP 429) condition that the gateway should retry.
/// </summary>
/// <remarks>
/// Implementations walk the exception chain to find a rate-limit indicator and,
/// where possible, extract the provider's <c>Retry-After</c> hint so the gateway
/// can honor it precisely instead of falling back to exponential backoff.
/// Pluggable so different providers (OpenAI, Anthropic-direct, Copilot, etc.)
/// can surface their own rate-limit shapes.
/// </remarks>
internal interface ILlmRateLimitClassifier
{
    /// <summary>
    /// Returns <c>true</c> if <paramref name="exception"/> indicates a rate-limit
    /// condition that should be retried. <paramref name="retryAfter"/> is set to
    /// the provider-supplied wait duration when available.
    /// </summary>
    bool TryClassify(Exception exception, out TimeSpan? retryAfter);
}

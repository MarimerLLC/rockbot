using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// Wrapper around <see cref="IChatClient"/> for all LLM calls in an agent process.
/// Adds retry logic for known model-specific SDK quirks and routes every call through
/// the per-tier <c>LlmGateway</c> which caps concurrency and propagates cancellation.
///
/// Registered as transient so each consumer gets its own instance, but the gateway
/// is a singleton so all consumers share the per-tier concurrency budget.
///
/// To avoid starting background LLM work while the user is actively waiting
/// for a response, use <see cref="IUserActivityMonitor"/> instead of this interface.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Calls the LLM using the <see cref="ModelTier.Balanced"/> client.
    /// </summary>
    /// <remarks>
    /// <paramref name="cancellationToken"/> is mandatory: the gateway uses it to
    /// drain queued and in-flight calls when the caller is preempted (e.g. when
    /// a user message cancels the dream cycle). Callers without a natural ct
    /// MUST pass <see cref="CancellationToken.None"/> explicitly so the choice
    /// is intentional and visible in code review. See <c>design/llm-gateway.md</c>.
    /// </remarks>
    Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken);

    /// <summary>
    /// Calls the LLM using the client for the specified <paramref name="tier"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="cancellationToken"/> is mandatory: see the single-arg overload.
    /// </remarks>
    Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ModelTier tier,
        ChatOptions? options,
        CancellationToken cancellationToken);
}

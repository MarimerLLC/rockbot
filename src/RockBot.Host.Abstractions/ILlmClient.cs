using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// Serialized gateway for all LLM calls in an agent process.
/// Ensures only one LLM request is in flight at a time, preventing concurrent
/// API calls from triggering rate limiting.
///
/// All subsystems that need to call the LLM — the main tool loop, background
/// memory enrichment, skill summary generation, and dreaming — should inject
/// and use this service rather than <see cref="IChatClient"/> directly.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// True when no LLM call is currently in flight.
    /// Low-priority callers (e.g. dreaming) can poll this before queuing
    /// to avoid blocking user-facing requests.
    /// </summary>
    bool IsIdle { get; }

    Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);
}

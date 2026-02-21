using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// Wrapper around <see cref="IChatClient"/> for all LLM calls in an agent process.
/// Adds retry logic for known model-specific SDK quirks. Registered as transient
/// so concurrent callers (user loop, background tasks, dreaming, session evaluation)
/// each get their own instance and never queue behind each other.
///
/// To avoid starting background LLM work while the user is actively waiting
/// for a response, use <see cref="IUserActivityMonitor"/> instead of this interface.
/// </summary>
public interface ILlmClient
{
    Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);
}

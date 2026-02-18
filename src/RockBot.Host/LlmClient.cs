using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// Default implementation of <see cref="ILlmClient"/>.
/// Serializes all LLM calls through a <see cref="SemaphoreSlim"/> so that
/// at most one request is in flight at any time. Background tasks (memory
/// enrichment, skill summary generation, dreaming) queue behind the active
/// user-facing request rather than racing with it and triggering rate limits.
/// </summary>
internal sealed class LlmClient(IChatClient chatClient) : ILlmClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc/>
    public bool IsIdle => _gate.CurrentCount > 0;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await chatClient.GetResponseAsync(messages, options, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}

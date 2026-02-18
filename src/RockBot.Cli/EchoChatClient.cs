using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RockBot.Cli;

/// <summary>
/// A simple IChatClient that echoes the last user message back.
/// Useful for testing the full pipeline without an LLM provider.
/// </summary>
internal sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var lastUserMessage = chatMessages
            .LastOrDefault(m => m.Role == ChatRole.User)
            ?.Text ?? "(no message)";

        var response = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, $"Echo: {lastUserMessage}"));

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

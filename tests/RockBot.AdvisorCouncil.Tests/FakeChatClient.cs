using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace RockBot.AdvisorCouncil.Tests;

/// <summary>
/// Test double that returns scripted responses. Tests configure responses in order or
/// by matching the user prompt. Tracks each call for assertions.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<string> _responses = new();
    private readonly Dictionary<Func<string, bool>, string> _matchers = new();

    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = new();

    public FakeChatClient EnqueueResponse(string text)
    {
        _responses.Enqueue(text);
        return this;
    }

    public FakeChatClient WhenUserContains(string substring, string response)
    {
        _matchers[m => m.Contains(substring, StringComparison.OrdinalIgnoreCase)] = response;
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgs = chatMessages.ToList();
        Calls.Add((msgs, options));

        var allText = string.Join("\n", msgs.Select(m => m.Text ?? string.Empty));
        foreach (var (predicate, response) in _matchers)
        {
            if (predicate(allText))
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        }

        var text = _responses.Count > 0 ? _responses.Dequeue() : "(no scripted response)";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
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

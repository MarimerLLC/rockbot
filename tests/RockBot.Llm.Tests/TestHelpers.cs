using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RockBot.Messaging;

namespace RockBot.Llm.Tests;

/// <summary>
/// Configurable stub IChatClient for unit testing.
/// </summary>
internal sealed class StubChatClient : IChatClient
{
    public ChatResponse? ResponseToReturn { get; set; }
    public Exception? ExceptionToThrow { get; set; }
    public List<(IEnumerable<ChatMessage> Messages, ChatOptions? Options)> Invocations { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Invocations.Add((chatMessages, options));

        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        return Task.FromResult(
            ResponseToReturn ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
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

/// <summary>
/// Captures all published envelopes for assertion.
/// </summary>
internal sealed class TrackingPublisher : IMessagePublisher
{
    public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = [];

    public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Published.Add((topic, envelope));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Shared test helpers.
/// </summary>
internal static class TestEnvelopeHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static MessageEnvelope CreateEnvelope<T>(
        T payload,
        string source = "test-source",
        string? correlationId = null,
        string? replyTo = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return MessageEnvelope.Create(
            messageType: typeof(T).FullName ?? typeof(T).Name,
            body: body,
            source: source,
            correlationId: correlationId,
            replyTo: replyTo);
    }
}

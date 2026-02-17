using System.Text.Json;
using RockBot.Messaging;

namespace RockBot.UserProxy.Tests;

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
/// Stub subscriber that captures the handler so tests can simulate incoming messages.
/// </summary>
internal sealed class StubSubscriber : IMessageSubscriber
{
    public Func<MessageEnvelope, CancellationToken, Task<MessageResult>>? CapturedHandler { get; private set; }
    public string? CapturedTopic { get; private set; }
    public string? CapturedSubscriptionName { get; private set; }

    public Task<ISubscription> SubscribeAsync(
        string topic,
        string subscriptionName,
        Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
        CancellationToken cancellationToken = default)
    {
        CapturedTopic = topic;
        CapturedSubscriptionName = subscriptionName;
        CapturedHandler = handler;
        return Task.FromResult<ISubscription>(new StubSubscription(topic, subscriptionName));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Minimal subscription stub.
/// </summary>
internal sealed class StubSubscription(string topic, string subscriptionName) : ISubscription
{
    public string Topic => topic;
    public string SubscriptionName => subscriptionName;
    public bool IsActive => true;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Captures displayed replies and errors for assertion.
/// </summary>
internal sealed class StubUserFrontend : IUserFrontend
{
    public List<AgentReply> DisplayedReplies { get; } = [];
    public List<string> DisplayedErrors { get; } = [];

    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        DisplayedReplies.Add(reply);
        return Task.CompletedTask;
    }

    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        DisplayedErrors.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Shared test helpers for creating envelopes.
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

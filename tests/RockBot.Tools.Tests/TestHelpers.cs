using System.Text.Json;
using RockBot.Messaging;
using RockBot.Tools;

namespace RockBot.Tools.Tests;

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

/// <summary>
/// Stub subscriber that records subscriptions and allows injecting messages.
/// </summary>
internal sealed class StubSubscriber : IMessageSubscriber
{
    public List<(string Topic, string SubscriptionName, Func<MessageEnvelope, CancellationToken, Task<MessageResult>> Handler)> Subscriptions { get; } = [];

    public Task<ISubscription> SubscribeAsync(
        string topic,
        string subscriptionName,
        Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
        CancellationToken cancellationToken = default)
    {
        Subscriptions.Add((topic, subscriptionName, handler));
        ISubscription sub = new StubSubscription(topic, subscriptionName);
        return Task.FromResult(sub);
    }

    /// <summary>
    /// Delivers a message to the handler for the given topic.
    /// </summary>
    public async Task<MessageResult> DeliverAsync(string topic, MessageEnvelope envelope, CancellationToken ct = default)
    {
        var sub = Subscriptions.FirstOrDefault(s => s.Topic == topic);
        if (sub.Handler is null)
            throw new InvalidOperationException($"No subscription found for topic '{topic}'");
        return await sub.Handler(envelope, ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class StubSubscription(string topic, string subscriptionName) : ISubscription
    {
        public string Topic => topic;
        public string SubscriptionName => subscriptionName;
        public bool IsActive => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Stub tool executor that returns a configurable response.
/// </summary>
internal sealed class StubToolExecutor : IToolExecutor
{
    public ToolInvokeResponse? ResponseToReturn { get; set; }
    public Exception? ExceptionToThrow { get; set; }
    public List<ToolInvokeRequest> Invocations { get; } = [];

    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Invocations.Add(request);

        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        return Task.FromResult(ResponseToReturn ?? new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = "stub result"
        });
    }
}

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Messaging;
using RockBot.Messaging.InProcess;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// The AG-UI proxy over the real HTTP pipeline, with the in-process bus in place of RabbitMQ. The
/// proxy's own <see cref="UserProxyService"/> runs, so a test's fake agent talks to it over the
/// same topics a real agent would.
/// </summary>
internal sealed class AgUiTestHost(params (string Key, string Value)[] settings) : WebApplicationFactory<Program>
{
    public const string AgentName = "RockBot";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("UserProxy:ProxyId", "agui-test");
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            // Everything RabbitMQ: the publisher and subscriber, and the DLQ reporter that would
            // dial the broker's management API.
            var rabbit = services
                .Where(d => d.ServiceType == typeof(IMessagePublisher)
                    || d.ServiceType == typeof(IMessageSubscriber)
                    || (d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType?.Namespace?.StartsWith("RockBot.Messaging.RabbitMQ", StringComparison.Ordinal) == true))
                .ToList();
            foreach (var descriptor in rabbit)
                services.Remove(descriptor);

            services.AddRockBotInProcessMessaging();
        });
    }

    public IMessagePublisher Publisher => Services.GetRequiredService<IMessagePublisher>();

    public IMessageSubscriber Subscriber => Services.GetRequiredService<IMessageSubscriber>();
}

/// <summary>
/// Stands in for the agent on the bus. Records what it receives; a test decides what it sends
/// back by setting <see cref="OnMessage"/>.
/// </summary>
internal sealed class FakeAgent : IAsyncDisposable
{
    private readonly IMessagePublisher _publisher;
    private readonly List<ISubscription> _subscriptions = [];
    private readonly Lock _lock = new();
    private readonly List<ReceivedMessage> _messages = [];
    private readonly TaskCompletionSource<CancelSessionRequest> _cancel = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private FakeAgent(IMessagePublisher publisher) => _publisher = publisher;

    public sealed record ReceivedMessage(MessageEnvelope Envelope, UserMessage Message);

    /// <summary>What the agent does with a user message. The default answers "Echo: …".</summary>
    public Func<FakeAgent, ReceivedMessage, Task> OnMessage { get; set; } =
        (agent, received) => agent.ReplyAsync(received, $"Echo: {received.Message.Content}");

    /// <summary>What the agent does with a cancel request, after recording it.</summary>
    public Func<FakeAgent, CancelSessionRequest, Task>? OnCancel { get; set; }

    /// <summary>The history the agent returns, or null to leave history requests unanswered.</summary>
    public ConversationHistoryResponse? History { get; set; }

    public IReadOnlyList<ReceivedMessage> Messages
    {
        get { lock (_lock) return [.. _messages]; }
    }

    /// <summary>Completes with the first cancel request the agent receives.</summary>
    public Task<CancelSessionRequest> CancelRequested => _cancel.Task;

    public static async Task<FakeAgent> StartAsync(AgUiTestHost host)
    {
        var agent = new FakeAgent(host.Publisher);
        var subscriber = host.Subscriber;

        agent._subscriptions.Add(await subscriber.SubscribeAsync(
            $"{UserProxyTopics.UserMessage}.{AgUiTestHost.AgentName}", "fake-agent.messages",
            async (envelope, _) =>
            {
                var received = new ReceivedMessage(envelope, envelope.GetPayload<UserMessage>()!);
                lock (agent._lock) agent._messages.Add(received);
                await agent.OnMessage(agent, received);
                return MessageResult.Ack;
            }));

        agent._subscriptions.Add(await subscriber.SubscribeAsync(
            $"{UserProxyTopics.CancelSession}.{AgUiTestHost.AgentName}", "fake-agent.cancel",
            async (envelope, _) =>
            {
                var request = envelope.GetPayload<CancelSessionRequest>()!;
                agent._cancel.TrySetResult(request);
                if (agent.OnCancel is { } onCancel)
                    await onCancel(agent, request);
                return MessageResult.Ack;
            }));

        agent._subscriptions.Add(await subscriber.SubscribeAsync(
            $"{UserProxyTopics.ConversationHistoryRequest}.{AgUiTestHost.AgentName}", "fake-agent.history",
            async (envelope, ct) =>
            {
                if (agent.History is { } history && envelope.ReplyTo is { } replyTo)
                {
                    await agent._publisher.PublishAsync(replyTo,
                        history.ToEnvelope<ConversationHistoryResponse>(source: AgUiTestHost.AgentName, correlationId: envelope.CorrelationId),
                        ct);
                }
                return MessageResult.Ack;
            }));

        return agent;
    }

    /// <summary>Publishes <paramref name="payload"/> to the turn's reply topic, with its correlation id.</summary>
    public Task SendAsync<T>(ReceivedMessage to, T payload) =>
        _publisher.PublishAsync(to.Envelope.ReplyTo!,
            payload.ToEnvelope<T>(source: AgUiTestHost.AgentName, correlationId: to.Envelope.CorrelationId));

    public Task ReplyAsync(ReceivedMessage to, string content, bool isFinal = true, ToolCallInfo? toolCall = null) =>
        SendAsync(to, new AgentReply
        {
            Content = content, SessionId = to.Message.SessionId, AgentName = AgUiTestHost.AgentName, IsFinal = isFinal, ToolCall = toolCall,
        });

    public Task RunStartedAsync(ReceivedMessage to) => SendAsync(to, new RunStarted
    {
        ThreadId = to.Message.SessionId, RunId = to.Envelope.CorrelationId!, AgentName = AgUiTestHost.AgentName,
        HeartbeatIntervalSeconds = 10, Timestamp = DateTimeOffset.UtcNow,
    });

    public Task RunFinishedAsync(ReceivedMessage to, string outcome, string? error = null) => SendAsync(to, new RunFinished
    {
        ThreadId = to.Message.SessionId, RunId = to.Envelope.CorrelationId!, Outcome = outcome, Error = error,
        Timestamp = DateTimeOffset.UtcNow,
    });

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions)
            await subscription.DisposeAsync();
    }
}

/// <summary>Reads a run's server-sent events.</summary>
internal static class Sse
{
    /// <summary>The JSON of every <c>data:</c> event in <paramref name="body"/>, in order.</summary>
    public static List<JsonElement> Events(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(block => block.StartsWith("data: ", StringComparison.Ordinal))
            .Select(block => JsonDocument.Parse(block["data: ".Length..]).RootElement.Clone())
            .ToList();

    public static string Type(JsonElement e) => e.GetProperty("type").GetString()!;

    public static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.GetString() : null;
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

/// <summary>
/// AgentHost passes each topic's <see cref="SubscriptionOptions"/> through to the
/// subscriber, so a fan-out topic's queue gets its retention (issue #654).
/// </summary>
[TestClass]
public class AgentHostSubscriptionTests
{
    [TestMethod]
    public async Task StartAsync_PassesTopicOptionsToSubscriber()
    {
        var retention = new SubscriptionOptions
        {
            MessageTtl = TimeSpan.FromMinutes(10),
            IdleExpiry = TimeSpan.FromHours(24),
            DeadLetter = false,
        };
        var options = new AgentHostOptions();
        options.Topics.Add(new TopicSubscription("agent.task.status", 1, retention));
        var subscriber = new CapturingSubscriber();

        await CreateHost(subscriber, options).StartAsync(CancellationToken.None);

        var (topic, name, captured) = subscriber.Calls.Single();
        Assert.AreEqual("agent.task.status", topic);
        Assert.AreEqual("my-agent.agent-task-status", name);
        Assert.AreEqual(retention, captured);
    }

    [TestMethod]
    public async Task StartAsync_TopicWithoutOptions_UsesDefaultsAndConcurrency()
    {
        var options = new AgentHostOptions();
        options.Topics.Add(new TopicSubscription("subagent.result.my-agent", 4));
        var subscriber = new CapturingSubscriber();

        await CreateHost(subscriber, options).StartAsync(CancellationToken.None);

        var captured = subscriber.Calls.Single().Options;
        Assert.AreEqual(new SubscriptionOptions { DispatchConcurrency = 4 }, captured);
    }

    [TestMethod]
    public async Task StartAsync_TopicDispatchConcurrencyWinsOverOptions()
    {
        var options = new AgentHostOptions();
        options.Topics.Add(new TopicSubscription(
            "x.topic", 3, new SubscriptionOptions { DispatchConcurrency = 1, DeadLetter = false }));
        var subscriber = new CapturingSubscriber();

        await CreateHost(subscriber, options).StartAsync(CancellationToken.None);

        var captured = subscriber.Calls.Single().Options;
        Assert.AreEqual(3, captured.DispatchConcurrency);
        Assert.IsFalse(captured.DeadLetter);
    }

    private static AgentHost CreateHost(IMessageSubscriber subscriber, AgentHostOptions options) =>
        new(subscriber,
            new NoOpPipeline(),
            new EmptyWipTracker(),
            new AgentIdentity("my-agent"),
            Options.Create(options),
            Options.Create(new WipOptions()),
            NullLogger<AgentHost>.Instance);

    private sealed class CapturingSubscriber : IMessageSubscriber
    {
        public List<(string Topic, string Name, SubscriptionOptions Options)> Calls { get; } = [];

        public Task<ISubscription> SubscribeAsync(
            string topic, string subscriptionName,
            Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
            CancellationToken cancellationToken = default,
            int dispatchConcurrency = 1)
            => SubscribeAsync(topic, subscriptionName, handler,
                new SubscriptionOptions { DispatchConcurrency = dispatchConcurrency }, cancellationToken);

        public Task<ISubscription> SubscribeAsync(
            string topic, string subscriptionName,
            Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
            SubscriptionOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((topic, subscriptionName, options));
            return Task.FromResult<ISubscription>(new StubSubscription());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSubscription : ISubscription
    {
        public string Topic => string.Empty;
        public string SubscriptionName => string.Empty;
        public bool IsActive => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoOpPipeline : IMessagePipeline
    {
        public Task<MessageResult> DispatchAsync(MessageEnvelope envelope, CancellationToken cancellationToken) =>
            Task.FromResult(MessageResult.Ack);
    }

    private sealed class EmptyWipTracker : IWipTracker
    {
        public Task<WipEntry> BeginAsync(MessageEnvelope envelope, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task CompleteAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task AbandonAsync(string messageId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WipEntry>> GetIncompleteAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WipEntry>>([]);
    }
}

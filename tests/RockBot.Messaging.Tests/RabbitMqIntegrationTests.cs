using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;
using RockBot.Messaging.RabbitMQ;

namespace RockBot.Messaging.Tests;

/// <summary>
/// Integration tests that require a running RabbitMQ instance.
/// Set the ROCKBOT_RABBITMQ_HOST environment variable to enable these tests.
/// Skipped when RabbitMQ is not available.
/// </summary>
[TestClass]
public class RabbitMqIntegrationTests : IAsyncDisposable
{
    private readonly ServiceProvider? _provider;
    private readonly bool _rabbitAvailable;

    public RabbitMqIntegrationTests()
    {
        var host = Environment.GetEnvironmentVariable("ROCKBOT_RABBITMQ_HOST");
        _rabbitAvailable = !string.IsNullOrEmpty(host);

        if (!_rabbitAvailable) return;

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddRockBotRabbitMq(opts =>
        {
            opts.HostName = host!;
            opts.ExchangeName = $"rockbot-test-{Guid.NewGuid():N}";
            opts.DeadLetterExchangeName = $"rockbot-test-dlx-{Guid.NewGuid():N}";
        });

        _provider = services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task PublishAndSubscribe_RoundTrip()
    {
        if (!_rabbitAvailable)
        {
            Assert.Inconclusive("RabbitMQ not available (set ROCKBOT_RABBITMQ_HOST)");
            return;
        }

        var publisher = _provider!.GetRequiredService<IMessagePublisher>();
        var subscriber = _provider!.GetRequiredService<IMessageSubscriber>();

        var received = new TaskCompletionSource<MessageEnvelope>();

        await using var subscription = await subscriber.SubscribeAsync(
            topic: "test.roundtrip",
            subscriptionName: $"test-{Guid.NewGuid():N}",
            handler: (envelope, ct) =>
            {
                received.TrySetResult(envelope);
                return Task.FromResult(MessageResult.Ack);
            });

        Assert.IsTrue(subscription.IsActive);

        var payload = new TestMessage("integration test", DateTimeOffset.UtcNow);
        var outgoing = payload.ToEnvelope("test-publisher", correlationId: "test-corr");

        await publisher.PublishAsync("test.roundtrip", outgoing);

        var incoming = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(outgoing.MessageId, incoming.MessageId);
        Assert.AreEqual("test-corr", incoming.CorrelationId);
        Assert.AreEqual("test-publisher", incoming.Source);

        var body = incoming.GetPayload<TestMessage>();
        Assert.IsNotNull(body);
        Assert.AreEqual("integration test", body.Text);
    }

    [TestMethod]
    public async Task WildcardSubscription_ReceivesMatchingTopics()
    {
        if (!_rabbitAvailable)
        {
            Assert.Inconclusive("RabbitMQ not available (set ROCKBOT_RABBITMQ_HOST)");
            return;
        }

        var publisher = _provider!.GetRequiredService<IMessagePublisher>();
        var subscriber = _provider!.GetRequiredService<IMessageSubscriber>();

        var messages = new List<MessageEnvelope>();
        var allReceived = new TaskCompletionSource();

        await using var subscription = await subscriber.SubscribeAsync(
            topic: "agent.*",
            subscriptionName: $"test-wildcard-{Guid.NewGuid():N}",
            handler: (envelope, ct) =>
            {
                lock (messages)
                {
                    messages.Add(envelope);
                    if (messages.Count >= 2)
                        allReceived.TrySetResult();
                }
                return Task.FromResult(MessageResult.Ack);
            });

        var msg1 = new TestMessage("task", DateTimeOffset.UtcNow)
            .ToEnvelope("agent-a");
        var msg2 = new TestMessage("response", DateTimeOffset.UtcNow)
            .ToEnvelope("agent-b");

        await publisher.PublishAsync("agent.task", msg1);
        await publisher.PublishAsync("agent.response", msg2);

        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, messages.Count);
    }

    private record TestMessage(string Text, DateTimeOffset SentAt);

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;
using RockBot.Messaging.InProcess;

namespace RockBot.Messaging.Tests;

[TestClass]
public class InProcessMessagingTests
{
    private ServiceProvider _provider = null!;
    private IMessagePublisher _publisher = null!;
    private IMessageSubscriber _subscriber = null!;

    [TestInitialize]
    public void Initialize()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddRockBotInProcessMessaging();
        _provider = services.BuildServiceProvider();
        _publisher = _provider.GetRequiredService<IMessagePublisher>();
        _subscriber = _provider.GetRequiredService<IMessageSubscriber>();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _provider.DisposeAsync();
    }

    [TestMethod]
    public async Task PublishAndSubscribe_RoundTrip()
    {
        var tcs = new TaskCompletionSource<MessageEnvelope>();

        await _subscriber.SubscribeAsync("test.topic", "sub1", (env, ct) =>
        {
            tcs.TrySetResult(env);
            return Task.FromResult(MessageResult.Ack);
        });

        var sent = MessageEnvelope.Create("test.message", new byte[] { 1, 2, 3 }, "test-source");
        await _publisher.PublishAsync("test.topic", sent);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(sent.MessageId, received.MessageId);
    }

    [TestMethod]
    public async Task WildcardStar_MatchesSingleSegment()
    {
        var matched = new List<string>();
        var tcs = new TaskCompletionSource<bool>();

        await _subscriber.SubscribeAsync("agent.*", "sub2", (env, ct) =>
        {
            lock (matched) matched.Add(env.MessageType);
            if (matched.Count >= 1) tcs.TrySetResult(true);
            return Task.FromResult(MessageResult.Ack);
        });

        // Should match: agent.task (single segment after agent.)
        var match = MessageEnvelope.Create("match", Array.Empty<byte>(), "src");
        await _publisher.PublishAsync("agent.task", match);

        // Should NOT match: agent.task.x (two segments after agent.)
        var noMatch = MessageEnvelope.Create("nomatch", Array.Empty<byte>(), "src");
        await _publisher.PublishAsync("agent.task.x", noMatch);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200); // Brief wait to ensure no extra messages arrive

        lock (matched)
        {
            Assert.AreEqual(1, matched.Count, "Only agent.task should be matched by agent.*");
            Assert.AreEqual("match", matched[0]);
        }
    }

    [TestMethod]
    public async Task WildcardHash_MatchesZeroOrMoreSegments()
    {
        var received = new List<string>();
        // agent.# should match: agent, agent.task, agent.task.x (zero or more segments after agent.)
        // Actually: the topic "agent" matches pattern "agent.#" because # matches zero segments.
        // "agent.task" matches (one segment), "agent.task.x" matches (two segments).

        var tcs = new TaskCompletionSource<bool>();

        await _subscriber.SubscribeAsync("agent.#", "sub3", (env, ct) =>
        {
            lock (received) { received.Add(env.MessageType); if (received.Count >= 3) tcs.TrySetResult(true); }
            return Task.FromResult(MessageResult.Ack);
        });

        var env1 = MessageEnvelope.Create("zero", Array.Empty<byte>(), "src");
        var env2 = MessageEnvelope.Create("one", Array.Empty<byte>(), "src");
        var env3 = MessageEnvelope.Create("two", Array.Empty<byte>(), "src");

        await _publisher.PublishAsync("agent", env1);
        await _publisher.PublishAsync("agent.task", env2);
        await _publisher.PublishAsync("agent.task.x", env3);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (received)
        {
            CollectionAssert.Contains(received, "zero");
            CollectionAssert.Contains(received, "one");
            CollectionAssert.Contains(received, "two");
        }
    }

    [TestMethod]
    public async Task MultipleSubscriptions_EachReceivesMessage()
    {
        var tcs1 = new TaskCompletionSource<MessageEnvelope>();
        var tcs2 = new TaskCompletionSource<MessageEnvelope>();

        await _subscriber.SubscribeAsync("shared.topic", "sub4a", (env, ct) =>
        {
            tcs1.TrySetResult(env);
            return Task.FromResult(MessageResult.Ack);
        });

        await _subscriber.SubscribeAsync("shared.topic", "sub4b", (env, ct) =>
        {
            tcs2.TrySetResult(env);
            return Task.FromResult(MessageResult.Ack);
        });

        var sent = MessageEnvelope.Create("shared.msg", Array.Empty<byte>(), "src");
        await _publisher.PublishAsync("shared.topic", sent);

        var r1 = await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var r2 = await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(sent.MessageId, r1.MessageId);
        Assert.AreEqual(sent.MessageId, r2.MessageId);
    }

    [TestMethod]
    public async Task Retry_ReenqueuesUpToMaxRetries()
    {
        var callCount = 0;
        var discarded = new TaskCompletionSource<int>();

        await _subscriber.SubscribeAsync("retry.topic", "sub5", (env, ct) =>
        {
            var count = Interlocked.Increment(ref callCount);
            // Always return Retry — after MaxRetries (3) attempts, it should be discarded
            // The pump calls handler on original enqueue (1) + 3 retries = 4 total calls
            if (count >= 4) discarded.TrySetResult(count);
            return Task.FromResult(MessageResult.Retry);
        });

        var msg = MessageEnvelope.Create("retry.msg", Array.Empty<byte>(), "src");
        await _publisher.PublishAsync("retry.topic", msg);

        // Wait for all retries to exhaust (initial call + 3 retries = 4 calls)
        var finalCount = await discarded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(4, finalCount);
    }

    [TestMethod]
    public async Task DeadLetter_DiscardsMessage()
    {
        var callCount = 0;
        var tcs = new TaskCompletionSource<bool>();

        await _subscriber.SubscribeAsync("dl.topic", "sub6", (env, ct) =>
        {
            Interlocked.Increment(ref callCount);
            tcs.TrySetResult(true);
            return Task.FromResult(MessageResult.DeadLetter);
        });

        var msg = MessageEnvelope.Create("dl.msg", Array.Empty<byte>(), "src");
        await _publisher.PublishAsync("dl.topic", msg);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200); // Ensure no re-delivery happens

        Assert.AreEqual(1, callCount, "Dead-lettered message should be processed exactly once");
    }

    [TestMethod]
    public async Task DisposeSubscription_StopsDelivery()
    {
        var deliveryCount = 0;
        var firstDelivery = new TaskCompletionSource<bool>();

        var subscription = await _subscriber.SubscribeAsync("dispose.topic", "sub7", (env, ct) =>
        {
            Interlocked.Increment(ref deliveryCount);
            firstDelivery.TrySetResult(true);
            return Task.FromResult(MessageResult.Ack);
        });

        // Publish first message — should be received
        var msg1 = MessageEnvelope.Create("msg1", Array.Empty<byte>(), "src");
        await _publisher.PublishAsync("dispose.topic", msg1);
        await firstDelivery.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Dispose the subscription
        await subscription.DisposeAsync();
        Assert.IsFalse(subscription.IsActive);

        // Publish second message after dispose — should NOT be received
        var msg2 = MessageEnvelope.Create("msg2", Array.Empty<byte>(), "src");
        await _publisher.PublishAsync("dispose.topic", msg2);

        await Task.Delay(500); // Give time for any erroneous delivery

        Assert.AreEqual(1, deliveryCount, "No messages should be delivered after subscription is disposed");
    }
}

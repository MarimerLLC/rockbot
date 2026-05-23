using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Messaging;

namespace RockBot.UserProxy.WorkIqAuth.Tests;

[TestClass]
public class WorkIqAuthStatusListenerTests
{
    [TestMethod]
    public async Task Start_SubscribesToExpiredTopic()
    {
        var subscriber = new StubMessageSubscriber();
        var listener = new WorkIqAuthStatusListener(
            subscriber, NullLogger<WorkIqAuthStatusListener>.Instance);

        await listener.StartAsync(CancellationToken.None);

        Assert.AreEqual(WorkIqAuthTopics.Expired, subscriber.Topic);

        await listener.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task ExpiredEvent_FiresOnEnvelopeDelivery()
    {
        var subscriber = new StubMessageSubscriber();
        var listener = new WorkIqAuthStatusListener(
            subscriber, NullLogger<WorkIqAuthStatusListener>.Instance);
        await listener.StartAsync(CancellationToken.None);

        WorkIqAuthExpired? captured = null;
        listener.Expired += (_, msg) => captured = msg;

        var payload = new WorkIqAuthExpired { AccountId = "acct@example.com", Reason = "refresh revoked" };
        var result = await subscriber.Handler!(payload.ToEnvelope(source: "agent.workiq"), CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
        Assert.IsNotNull(captured);
        Assert.AreEqual("acct@example.com", captured!.AccountId);
        Assert.AreEqual("refresh revoked", captured.Reason);
        Assert.AreSame(captured, listener.LastExpired);

        await listener.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task ExpiredEvent_MalformedPayload_DeadLetters()
    {
        var subscriber = new StubMessageSubscriber();
        var listener = new WorkIqAuthStatusListener(
            subscriber, NullLogger<WorkIqAuthStatusListener>.Instance);
        await listener.StartAsync(CancellationToken.None);

        // Publish an envelope with the wrong payload type. Provider-side
        // GetPayload returns null for shape mismatch; the listener should
        // dead-letter, not crash.
        var bogus = new { Hello = "world" };
        var result = await subscriber.Handler!(
            bogus.ToEnvelope(source: "noise"), CancellationToken.None);

        Assert.AreEqual(MessageResult.DeadLetter, result);
        Assert.IsNull(listener.LastExpired);

        await listener.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task ClearLastExpired_ResetsState()
    {
        var subscriber = new StubMessageSubscriber();
        var listener = new WorkIqAuthStatusListener(
            subscriber, NullLogger<WorkIqAuthStatusListener>.Instance);
        await listener.StartAsync(CancellationToken.None);

        var payload = new WorkIqAuthExpired { AccountId = "acct" };
        await subscriber.Handler!(payload.ToEnvelope(source: "agent"), CancellationToken.None);
        Assert.IsNotNull(listener.LastExpired);

        listener.ClearLastExpired();
        Assert.IsNull(listener.LastExpired);

        await listener.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task ExpiredHandler_ExceptionDoesNotPropagate()
    {
        var subscriber = new StubMessageSubscriber();
        var listener = new WorkIqAuthStatusListener(
            subscriber, NullLogger<WorkIqAuthStatusListener>.Instance);
        await listener.StartAsync(CancellationToken.None);

        listener.Expired += (_, _) => throw new InvalidOperationException("buggy UI");

        var payload = new WorkIqAuthExpired { AccountId = "acct" };
        var result = await subscriber.Handler!(
            payload.ToEnvelope(source: "agent"), CancellationToken.None);

        // The listener must still ack — a buggy handler can't break the subscriber.
        Assert.AreEqual(MessageResult.Ack, result);

        await listener.StopAsync(CancellationToken.None);
    }
}

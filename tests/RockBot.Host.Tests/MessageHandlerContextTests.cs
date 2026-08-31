using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Host.Tests;

[TestClass]
public class MessageHandlerContextTests
{
    [TestMethod]
    public void Result_DefaultsToAck()
    {
        var context = CreateContext();

        Assert.AreEqual(MessageResult.Ack, context.Result);
    }

    [TestMethod]
    public void Result_CanBeChanged()
    {
        var context = CreateContext();

        context.Result = MessageResult.Retry;
        Assert.AreEqual(MessageResult.Retry, context.Result);

        context.Result = MessageResult.DeadLetter;
        Assert.AreEqual(MessageResult.DeadLetter, context.Result);
    }

    [TestMethod]
    public void Items_DefaultsToEmpty()
    {
        var context = CreateContext();

        Assert.AreEqual(0, context.Items.Count);
    }

    [TestMethod]
    public void Items_IsMutable()
    {
        var context = CreateContext();

        context.Items["key"] = "value";
        Assert.AreEqual("value", context.Items["key"]);
    }

    [TestMethod]
    public void Properties_AreAccessible()
    {
        var envelope = MessageEnvelope.Create("test", new byte[] { 1 }, "src");
        var identity = new AgentIdentity("test-agent");
        using var cts = new CancellationTokenSource();

        var context = new MessageHandlerContext
        {
            Envelope = envelope,
            Agent = identity,
            Services = new EmptyServiceProvider(),
            CancellationToken = cts.Token
        };

        Assert.AreSame(envelope, context.Envelope);
        Assert.AreSame(identity, context.Agent);
        Assert.AreEqual(cts.Token, context.CancellationToken);
    }

    private static MessageHandlerContext CreateContext() => new()
    {
        Envelope = MessageEnvelope.Create("test", new byte[] { 1 }, "src"),
        Agent = new AgentIdentity("test-agent"),
        Services = new EmptyServiceProvider(),
        CancellationToken = CancellationToken.None
    };

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

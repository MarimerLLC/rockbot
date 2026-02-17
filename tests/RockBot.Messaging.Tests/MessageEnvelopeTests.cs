using RockBot.Messaging;

namespace RockBot.Messaging.Tests;

[TestClass]
public class MessageEnvelopeTests
{
    [TestMethod]
    public void Create_SetsDefaults()
    {
        var body = new byte[] { 1, 2, 3 };
        var envelope = MessageEnvelope.Create(
            messageType: "test.message",
            body: body,
            source: "test-agent");

        Assert.IsNotNull(envelope.MessageId);
        Assert.AreEqual("test.message", envelope.MessageType);
        Assert.AreEqual("test-agent", envelope.Source);
        Assert.IsNull(envelope.CorrelationId);
        Assert.IsNull(envelope.ReplyTo);
        Assert.IsNull(envelope.Destination);
        Assert.IsTrue(envelope.Timestamp <= DateTimeOffset.UtcNow);
        CollectionAssert.AreEqual(body, envelope.Body.ToArray());
        Assert.AreEqual(0, envelope.Headers.Count);
    }

    [TestMethod]
    public void Create_WithAllFields()
    {
        var headers = new Dictionary<string, string> { ["key"] = "value" };
        var envelope = MessageEnvelope.Create(
            messageType: "test.message",
            body: new byte[] { 1 },
            source: "agent-a",
            correlationId: "corr-123",
            replyTo: "agent-b",
            destination: "agent-c",
            headers: headers);

        Assert.AreEqual("corr-123", envelope.CorrelationId);
        Assert.AreEqual("agent-b", envelope.ReplyTo);
        Assert.AreEqual("agent-c", envelope.Destination);
        Assert.AreEqual("value", envelope.Headers["key"]);
    }

    [TestMethod]
    public void ToEnvelope_RoundTrips()
    {
        var payload = new TestPayload("hello", 42);
        var envelope = payload.ToEnvelope("test-agent", correlationId: "corr-1");

        var deserialized = envelope.GetPayload<TestPayload>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("hello", deserialized.Name);
        Assert.AreEqual(42, deserialized.Value);
        StringAssert.Contains(envelope.MessageType, "TestPayload");
        Assert.AreEqual("corr-1", envelope.CorrelationId);
    }

    [TestMethod]
    public void MessageIds_AreUnique()
    {
        var body = new byte[] { 1 };
        var env1 = MessageEnvelope.Create("test", body, "agent");
        var env2 = MessageEnvelope.Create("test", body, "agent");

        Assert.AreNotEqual(env1.MessageId, env2.MessageId);
    }

    private record TestPayload(string Name, int Value);
}

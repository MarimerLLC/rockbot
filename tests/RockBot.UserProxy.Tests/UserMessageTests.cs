using RockBot.Messaging;

namespace RockBot.UserProxy.Tests;

[TestClass]
public sealed class UserMessageTests
{
    [TestMethod]
    public void UserMessage_RoundTrips_ThroughEnvelope()
    {
        var original = new UserMessage
        {
            Content = "Hello agent",
            SessionId = "session-1",
            UserId = "user-1",
            TargetAgent = "agent-alpha"
        };

        var envelope = original.ToEnvelope<UserMessage>(source: "proxy");
        var deserialized = envelope.GetPayload<UserMessage>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(original.Content, deserialized.Content);
        Assert.AreEqual(original.SessionId, deserialized.SessionId);
        Assert.AreEqual(original.UserId, deserialized.UserId);
        Assert.AreEqual(original.TargetAgent, deserialized.TargetAgent);
    }

    [TestMethod]
    public void UserMessage_WithoutTargetAgent_RoundTrips()
    {
        var original = new UserMessage
        {
            Content = "Hello",
            SessionId = "session-1",
            UserId = "user-1"
        };

        var envelope = original.ToEnvelope<UserMessage>(source: "proxy");
        var deserialized = envelope.GetPayload<UserMessage>();

        Assert.IsNotNull(deserialized);
        Assert.IsNull(deserialized.TargetAgent);
    }

    [TestMethod]
    public void AgentReply_RoundTrips_ThroughEnvelope()
    {
        var original = new AgentReply
        {
            Content = "Hello human",
            SessionId = "session-1",
            AgentName = "agent-alpha",
            IsFinal = true,
            StructuredData = """{"key":"value"}""",
            ContentType = "text/plain"
        };

        var envelope = original.ToEnvelope<AgentReply>(source: "agent-alpha");
        var deserialized = envelope.GetPayload<AgentReply>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(original.Content, deserialized.Content);
        Assert.AreEqual(original.SessionId, deserialized.SessionId);
        Assert.AreEqual(original.AgentName, deserialized.AgentName);
        Assert.AreEqual(original.IsFinal, deserialized.IsFinal);
        Assert.AreEqual(original.StructuredData, deserialized.StructuredData);
        Assert.AreEqual(original.ContentType, deserialized.ContentType);
    }

    [TestMethod]
    public void AgentReply_Defaults_IsFinalToTrue()
    {
        var reply = new AgentReply
        {
            Content = "Reply",
            SessionId = "s",
            AgentName = "a"
        };

        Assert.IsTrue(reply.IsFinal);
    }

    [TestMethod]
    public void UserProxyTopics_HasExpectedValues()
    {
        Assert.AreEqual("user.message", UserProxyTopics.UserMessage);
        Assert.AreEqual("user.response", UserProxyTopics.UserResponse);
    }
}

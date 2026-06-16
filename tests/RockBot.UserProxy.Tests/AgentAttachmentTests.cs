using System.Text;
using System.Text.Json;
using RockBot.Messaging;

namespace RockBot.UserProxy.Tests;

[TestClass]
public sealed class AgentAttachmentTests
{
    [TestMethod]
    public void AgentReply_WithAttachments_RoundTrips_ThroughEnvelope()
    {
        var original = new AgentReply
        {
            Content = "Here's your chart",
            SessionId = "session-1",
            AgentName = "agent-alpha",
            IsFinal = true,
            Attachments =
            [
                new AgentAttachment { Mime = "image/png", Path = "chart.png", FileName = "Q3 chart.png" },
                new AgentAttachment { Mime = "application/pdf", Path = "report.pdf" }
            ]
        };

        var envelope = original.ToEnvelope<AgentReply>(source: "agent-alpha");
        var deserialized = envelope.GetPayload<AgentReply>();

        Assert.IsNotNull(deserialized);
        Assert.IsNotNull(deserialized.Attachments);
        Assert.AreEqual(2, deserialized.Attachments.Count);
        Assert.AreEqual("image/png", deserialized.Attachments[0].Mime);
        Assert.AreEqual("chart.png", deserialized.Attachments[0].Path);
        Assert.AreEqual("Q3 chart.png", deserialized.Attachments[0].FileName);
        Assert.AreEqual("application/pdf", deserialized.Attachments[1].Mime);
        Assert.AreEqual("report.pdf", deserialized.Attachments[1].Path);
        Assert.IsNull(deserialized.Attachments[1].FileName);
    }

    [TestMethod]
    public void AgentReply_WithoutAttachments_RoundTrips_AsNull()
    {
        var original = new AgentReply
        {
            Content = "Just text",
            SessionId = "s",
            AgentName = "a"
        };

        var envelope = original.ToEnvelope<AgentReply>(source: "a");
        var deserialized = envelope.GetPayload<AgentReply>();

        Assert.IsNotNull(deserialized);
        Assert.IsNull(deserialized.Attachments);
    }

    [TestMethod]
    public void AgentAttachment_Serializes_WithCamelCaseKeys()
    {
        var reply = new AgentReply
        {
            Content = "x",
            SessionId = "s",
            AgentName = "a",
            Attachments = [new AgentAttachment { Mime = "image/png", Path = "chart.png", FileName = "c.png" }]
        };

        var envelope = reply.ToEnvelope<AgentReply>(source: "a");
        var json = Encoding.UTF8.GetString(envelope.Body.Span);

        using var doc = JsonDocument.Parse(json);
        var attachments = doc.RootElement.GetProperty("attachments");
        Assert.AreEqual(1, attachments.GetArrayLength());
        var first = attachments[0];
        Assert.AreEqual("image/png", first.GetProperty("mime").GetString());
        Assert.AreEqual("chart.png", first.GetProperty("path").GetString());
        Assert.AreEqual("c.png", first.GetProperty("fileName").GetString());
    }
}

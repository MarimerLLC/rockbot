using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>Conversation history as an AG-UI <c>MESSAGES_SNAPSHOT</c>.</summary>
[TestClass]
public sealed class AgUiHistoryMapperTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 14, 9, 30, 0, TimeSpan.Zero);

    private static readonly ConversationHistoryResponse History = new()
    {
        Turns =
        [
            new ConversationHistoryTurn { Role = "user", Content = "Chart the → results", Timestamp = At },
            new ConversationHistoryTurn
            {
                Role = "assistant", Content = "Here **it** is", Timestamp = At.AddSeconds(5), AgentName = "RockBot",
                Attachments = [new AgentAttachment { Mime = "image/png", Path = "chart.png", FileName = "Chart" }],
            },
        ],
    };

    [TestMethod]
    public void Turns_BecomeUserAndAssistantMessages_InOrder()
    {
        var snapshot = AgUiHistoryMapper.ToSnapshot("agui-session", History);

        Assert.AreEqual(2, snapshot.Messages.Count);

        var user = snapshot.Messages[0] as AGUIUserMessage;
        Assert.IsNotNull(user);
        Assert.AreEqual("agui-session-0", user.Id);
        Assert.AreEqual("Chart the → results", user.Content.ToString());

        var assistant = snapshot.Messages[1] as AGUIAssistantMessage;
        Assert.IsNotNull(assistant);
        Assert.AreEqual("agui-session-1", assistant.Id);
        Assert.AreEqual("Here **it** is", assistant.Content);
        Assert.AreEqual("RockBot", assistant.Name);
    }

    [TestMethod]
    public void WhatAgUiHasNoFieldFor_IsUnderMetadataRockBot()
    {
        var json = JsonDocument.Parse(AgUiJson.Serialize(AgUiHistoryMapper.ToSnapshot("agui-session", History))).RootElement;

        Assert.AreEqual("MESSAGES_SNAPSHOT", json.GetProperty("type").GetString());
        var messages = json.GetProperty("messages");
        Assert.AreEqual("user", messages[0].GetProperty("role").GetString());
        Assert.AreEqual("assistant", messages[1].GetProperty("role").GetString());

        var userMeta = messages[0].GetProperty("metadata").GetProperty("rockbot");
        Assert.AreEqual(At, userMeta.GetProperty("timestamp").GetDateTimeOffset());
        Assert.IsFalse(userMeta.TryGetProperty("attachments", out _), "no attachments means no property");

        var meta = messages[1].GetProperty("metadata").GetProperty("rockbot");
        Assert.AreEqual("RockBot", meta.GetProperty("agentName").GetString());
        Assert.AreEqual("chart.png", meta.GetProperty("attachments")[0].GetProperty("path").GetString());
        Assert.AreEqual("image/png", meta.GetProperty("attachments")[0].GetProperty("mime").GetString());
    }

    [TestMethod]
    public void TheSnapshot_ParsesBackWithTheSdk()
    {
        var json = AgUiJson.Serialize(AgUiHistoryMapper.ToSnapshot("agui-session", History));

        var parsed = JsonSerializer.Deserialize(json, AGUIJsonSerializerContext.Default.BaseEvent) as MessagesSnapshotEvent;

        Assert.IsNotNull(parsed);
        Assert.AreEqual(2, parsed.Messages.Count);
        Assert.IsInstanceOfType<AGUIUserMessage>(parsed.Messages[0]);
        Assert.IsInstanceOfType<AGUIAssistantMessage>(parsed.Messages[1]);
    }

    [TestMethod]
    public void ARoleThatIsNotTheUsers_IsShownAsTheAgents()
    {
        var snapshot = AgUiHistoryMapper.ToSnapshot("t", new ConversationHistoryResponse
        {
            Turns = [new ConversationHistoryTurn { Role = "system", Content = "note", Timestamp = At }],
        });

        Assert.IsInstanceOfType<AGUIAssistantMessage>(snapshot.Messages[0]);
    }

    [TestMethod]
    public void EmptyHistory_IsAnEmptySnapshot()
    {
        var snapshot = AgUiHistoryMapper.ToSnapshot("t", new ConversationHistoryResponse { Turns = [] });

        Assert.AreEqual(0, snapshot.Messages.Count);
    }
}

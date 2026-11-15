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
    public void ATurnsOwnId_IsTheMessageId()
    {
        // #809: the live copy of the reply had the same id on the stream.
        var history = new ConversationHistoryResponse
        {
            Turns =
            [
                new ConversationHistoryTurn { Role = "user", Content = "hi", Timestamp = At, Id = "turn-a" },
                new ConversationHistoryTurn { Role = "assistant", Content = "hello", Timestamp = At },
            ],
        };

        var snapshot = AgUiHistoryMapper.ToSnapshot("agui-session", history);

        Assert.AreEqual("turn-a", snapshot.Messages[0].Id);
        Assert.AreEqual("agui-session-1", snapshot.Messages[1].Id, "an older agent's turn falls back to its position");
    }

    [TestMethod]
    public void SessionOriginAndSource_AreUnderMetadataRockBot()
    {
        var started = At.AddHours(-2);
        var history = new ConversationHistoryResponse
        {
            Turns =
            [
                new ConversationHistoryTurn
                {
                    Role = "assistant", Content = "**Daily summary**", Timestamp = At, AgentName = "RockBot",
                    Id = "r-1", SessionId = "scheduled",
                    Origin = new ReplyOrigin("scheduled", "daily-summary", started, "scheduled"),
                    Source = ReplySource.Scheduled,
                },
                new ConversationHistoryTurn
                {
                    Role = "user", Content = "[Subagent task abc completed]: done", Timestamp = At,
                    AgentName = "subagent-abc", SessionId = "agui-session", Source = ReplySource.Subagent("abc"),
                },
            ],
        };

        var json = JsonDocument.Parse(AgUiJson.Serialize(AgUiHistoryMapper.ToSnapshot("agui-session", history))).RootElement;
        var messages = json.GetProperty("messages");

        var report = messages[0].GetProperty("metadata").GetProperty("rockbot");
        Assert.AreEqual("scheduled", report.GetProperty("sessionId").GetString());
        Assert.AreEqual("scheduled", report.GetProperty("origin").GetProperty("channel").GetString());
        Assert.AreEqual("daily-summary", report.GetProperty("origin").GetProperty("promptSummary").GetString());
        Assert.AreEqual(started, report.GetProperty("origin").GetProperty("startedAt").GetDateTimeOffset());
        Assert.AreEqual("scheduled", report.GetProperty("source").GetProperty("kind").GetString());

        var result = messages[1].GetProperty("metadata").GetProperty("rockbot");
        Assert.AreEqual("user", messages[1].GetProperty("role").GetString(), "a stored result keeps its role");
        Assert.AreEqual("subagent", result.GetProperty("source").GetProperty("kind").GetString());
        Assert.AreEqual("abc", result.GetProperty("source").GetProperty("taskId").GetString());
        Assert.IsFalse(result.TryGetProperty("origin", out _), "no origin means no property");
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
    public void ARoleThatIsNeitherTheUsersNorTheSystems_IsShownAsTheAgents()
    {
        var snapshot = AgUiHistoryMapper.ToSnapshot("t", new ConversationHistoryResponse
        {
            Turns = [new ConversationHistoryTurn { Role = "tool", Content = "note", Timestamp = At }],
        });

        Assert.IsInstanceOfType<AGUIAssistantMessage>(snapshot.Messages[0]);
    }

    /// <summary>
    /// The agent stores prompts it wrote to itself as system turns (a consolidation after a
    /// subagent finished). They must not come back as things the agent said to the user.
    /// </summary>
    [TestMethod]
    public void ASystemTurn_KeepsItsRole_SoAClientCanLeaveItOut()
    {
        var snapshot = AgUiHistoryMapper.ToSnapshot("t", new ConversationHistoryResponse
        {
            Turns =
            [
                new ConversationHistoryTurn { Role = "user", Content = "Go", Timestamp = At },
                new ConversationHistoryTurn { Role = "System", Content = "[Consolidate the subagent's findings]", Timestamp = At },
                new ConversationHistoryTurn { Role = "assistant", Content = "Done.", Timestamp = At, AgentName = "RockBot" },
            ],
        });

        Assert.IsInstanceOfType<AGUISystemMessage>(snapshot.Messages[1]);
        var json = JsonDocument.Parse(AgUiJson.Serialize(snapshot)).RootElement.GetProperty("messages");
        CollectionAssert.AreEqual(
            new[] { "user", "system", "assistant" },
            json.EnumerateArray().Select(m => m.GetProperty("role").GetString()).ToArray());
        // Ids are positions in the thread, so the system turn still takes one.
        Assert.AreEqual("t-2", json[2].GetProperty("id").GetString());
        Assert.AreEqual("[Consolidate the subagent's findings]", json[1].GetProperty("content").GetString());
    }

    [TestMethod]
    public void EmptyHistory_IsAnEmptySnapshot()
    {
        var snapshot = AgUiHistoryMapper.ToSnapshot("t", new ConversationHistoryResponse { Turns = [] });

        Assert.AreEqual(0, snapshot.Messages.Count);
    }
}

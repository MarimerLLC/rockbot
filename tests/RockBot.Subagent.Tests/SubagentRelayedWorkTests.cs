using System.Text;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Subagent.Tests;

/// <summary>
/// A subagent's tool calls travel with its result to the primary's synthesis turn (#683), so the
/// completion check there sees the writes and uploads the relayed report describes instead of
/// judging them against the synthesis turn's own read-only calls.
/// </summary>
[TestClass]
public class SubagentRelayedWorkTests
{
    private static readonly IReadOnlyList<SubagentToolCallSummary> DeckCalls =
    [
        new("file_write", true, true, "path=talks/mcp-v2/deck.md"),
        new("mcp_invoke_tool → upload_file", true, true, "server_name=onedrive, tool_name=upload_file"),
        new("web_fetch", false, false, "url=https://example.test"),
    ];

    private static SubagentResultMessage Result(string taskId, IReadOnlyList<SubagentToolCallSummary>? calls, int? count) => new()
    {
        TaskId = taskId,
        SubagentSessionId = $"subagent-{taskId}",
        PrimarySessionId = "session/s1",
        Output = "Deck revised and uploaded.",
        IsSuccess = true,
        Timestamp = DateTimeOffset.UtcNow,
        ToolCalls = calls,
        ToolCallCount = count,
    };

    [TestMethod]
    public void ResultMessage_RoundTripsTheToolCalls()
    {
        var envelope = Result("abc123", DeckCalls, 7).ToEnvelope<SubagentResultMessage>(source: "subagent-abc123");
        var back = envelope.GetPayload<SubagentResultMessage>()!;

        Assert.AreEqual(7, back.ToolCallCount);
        CollectionAssert.AreEqual(DeckCalls.ToList(), back.ToolCalls!.ToList());
    }

    [TestMethod]
    public void ResultMessage_FromAnOlderBuild_HasNoToolCalls()
    {
        // A result published before #683 has no toolCalls / toolCallCount fields.
        const string json =
            "{\"taskId\":\"a\",\"subagentSessionId\":\"subagent-a\",\"primarySessionId\":\"session/s1\"," +
            "\"output\":\"done\",\"isSuccess\":true,\"timestamp\":\"2026-10-09T12:00:00Z\"," +
            "\"originatingUserRequest\":\"trim the deck\",\"description\":\"Trim the deck\"}";
        var envelope = MessageEnvelope.Create(
            messageType: typeof(SubagentResultMessage).FullName!,
            body: Encoding.UTF8.GetBytes(json),
            source: "subagent-a");

        var back = envelope.GetPayload<SubagentResultMessage>()!;
        Assert.AreEqual("a", back.TaskId);
        Assert.AreEqual("trim the deck", back.OriginatingUserRequest);
        Assert.IsNull(back.ToolCalls);
        Assert.IsNull(back.ToolCallCount);
    }

    [TestMethod]
    public void BuildRelayedWork_CarriesEachResultsCalls_AndOnlyItsOwnArtifacts()
    {
        var registry = new SessionWorkRegistry();
        registry.LinkSession("subagent-abc123", "session/s1");
        registry.LinkSession("subagent/abc123", "session/s1");
        registry.LinkSession("subagent-def456", "session/s1");
        registry.LinkSession("wisp-0123456789a", "subagent/abc123");

        // abc123's wisp uploaded the deck; def456 wrote an unrelated file; the primary wrote one too.
        registry.RecordToolCall("wisp-0123456789a", "mcp_invoke_tool",
        [
            new("server_name", "onedrive"), new("tool_name", "upload_file"),
            new("arguments", "{\"remote_path\":\"/Talks/deck.md\"}"),
        ], true);
        registry.RecordToolCall("subagent-def456", "file_write", [new("path", "research/notes.md"), new("content", "x")], true);
        registry.RecordToolCall("s1", "file_write", [new("path", "drafts/old-deck.md"), new("content", "x")], true);

        var work = SubagentResultHandler.BuildRelayedWork(
            [Result("abc123", DeckCalls, 3), Result("def456", null, null)], registry, "session/s1");

        Assert.AreEqual(2, work.Count);
        Assert.AreEqual("abc123", work[0].TaskId);
        Assert.AreSame(DeckCalls, work[0].ToolCalls);
        Assert.AreEqual(3, work[0].TotalToolCalls);
        Assert.AreEqual(1, work[0].Artifacts!.Count, string.Join("; ", work[0].Artifacts!));
        StringAssert.Contains(work[0].Artifacts![0], "/Talks/deck.md");

        Assert.IsNull(work[1].ToolCalls, "an older result reports no calls — the evaluator is told so");
        Assert.AreEqual(1, work[1].Artifacts!.Count);
        StringAssert.Contains(work[1].Artifacts![0], "research/notes.md");
        Assert.IsFalse(work.SelectMany(w => w.Artifacts!).Any(a => a.Contains("old-deck", StringComparison.Ordinal)),
            "the primary's own files are not credited to a subagent");
    }

    [TestMethod]
    public void BuildRelayedWork_WithoutARegistry_StillCarriesTheCalls()
    {
        var work = SubagentResultHandler.BuildRelayedWork([Result("abc123", DeckCalls, 3)], null, "session/s1");

        Assert.AreSame(DeckCalls, work.Single().ToolCalls);
        Assert.AreEqual(0, work.Single().Artifacts!.Count);
    }
}

using RockBot.Host;

namespace RockBot.Subagent.Tests;

[TestClass]
public class SubagentResultHandlerDegradedReplyTests
{
    private static SubagentResultMessage MakeResult(
        string taskId, bool isSuccess, string output, string? error = null) => new()
    {
        TaskId = taskId,
        SubagentSessionId = $"subagent-{taskId}",
        PrimarySessionId = "session/s1",
        Output = output,
        IsSuccess = isSuccess,
        Error = error,
        Timestamp = DateTimeOffset.UtcNow,
        BatchId = "batch-1"
    };

    [TestMethod]
    public void BuildDegradedReply_IncludesEverySubagentResult_AndCounts()
    {
        var results = new[]
        {
            MakeResult("a1", isSuccess: true,  output: "alpha findings"),
            MakeResult("b2", isSuccess: false, output: "(crash)", error: "Status: 503"),
            MakeResult("c3", isSuccess: true,  output: "gamma findings")
        };

        var content = SubagentResultHandler.BuildDegradedReplyContent(
            results, new InvalidOperationException("synthesis 503"));

        StringAssert.Contains(content, "2 succeeded, 1 failed");
        StringAssert.Contains(content, "synthesis 503");
        StringAssert.Contains(content, "Subagent a1");
        StringAssert.Contains(content, "alpha findings");
        StringAssert.Contains(content, "Subagent b2");
        StringAssert.Contains(content, "Status: 503");
        StringAssert.Contains(content, "Subagent c3");
        StringAssert.Contains(content, "gamma findings");
    }

    [TestMethod]
    public void BuildDegradedReply_EmptyOutput_RendersPlaceholder()
    {
        var results = new[] { MakeResult("only", isSuccess: false, output: "") };

        var content = SubagentResultHandler.BuildDegradedReplyContent(
            results, new Exception("boom"));

        StringAssert.Contains(content, "Subagent only");
        StringAssert.Contains(content, "(no output)");
    }

    // ── Where a consolidated result is delivered ────────────────────────────

    [TestMethod]
    public void BuildFinalReply_PatrolSession_RepliesInTheScheduledBroadcastSession()
    {
        var now = DateTimeOffset.Parse("2026-11-18T15:02:57Z");

        var reply = SubagentResultHandler.BuildFinalReply(
            "patrol/daily-operational-brief", "the brief", "RockBot", sessionOrigin: null,
            isSystemTask: false, now);

        Assert.AreEqual(WellKnownSessions.Scheduled, reply.SessionId);
        Assert.AreEqual(RockBot.UserProxy.ReplySourceKind.Scheduled, reply.Source?.Kind);
        Assert.IsTrue(reply.IsFinal);
        Assert.AreEqual("the brief", reply.Content);
        Assert.IsFalse(string.IsNullOrEmpty(reply.MessageId));
        Assert.IsNotNull(reply.Origin);
        Assert.AreEqual("scheduled", reply.Origin.Channel);
        Assert.AreEqual("daily-operational-brief", reply.Origin.PromptSummary);
        Assert.AreEqual(now, reply.Origin.StartedAt);
        Assert.AreEqual(WellKnownSessions.Scheduled, reply.Origin.SessionId);
        Assert.IsTrue(WellKnownSessions.IsBroadcast(reply.SessionId));
    }

    [TestMethod]
    public void BuildFinalReply_SystemPatrolSession_RepliesInTheSystemBroadcastSession()
    {
        var reply = SubagentResultHandler.BuildFinalReply(
            "patrol/heartbeat-patrol", "findings", "RockBot", sessionOrigin: null,
            isSystemTask: true, DateTimeOffset.UtcNow);

        Assert.AreEqual(WellKnownSessions.ScheduledSystem, reply.SessionId);
        Assert.AreEqual(WellKnownSessions.ScheduledSystem, reply.Origin?.SessionId);
    }

    [TestMethod]
    public void BuildFinalReply_UserSession_StaysInItsSessionWithItsOrigin()
    {
        var origin = new RockBot.UserProxy.ReplyOrigin("blazor", "Compare the vendors", DateTimeOffset.UtcNow, "blazor-session");

        var reply = SubagentResultHandler.BuildFinalReply(
            "blazor-session", "comparison", "RockBot", origin, isSystemTask: false, DateTimeOffset.UtcNow);

        Assert.AreEqual("blazor-session", reply.SessionId);
        Assert.AreEqual(RockBot.UserProxy.ReplySourceKind.Primary, reply.Source?.Kind);
        Assert.AreSame(origin, reply.Origin);
    }

    // ── #418: how a consolidated result is saved ────────────────────────────

    [TestMethod]
    public void ArchiveSourceAndLabel_PatrolSession_IsTheScheduledTasksReport()
    {
        var (source, label) = SubagentResultHandler.ArchiveSourceAndLabel(
            "patrol/morning-briefing", [MakeResult("a1", true, "x")], origin: null);

        Assert.AreEqual(SavedResponseSources.Scheduled, source);
        Assert.AreEqual("morning-briefing", label);
    }

    [TestMethod]
    public void ArchiveSourceAndLabel_UserSession_PrefersTheSpawningRequestOverTheSessionOrigin()
    {
        var result = MakeResult("a1", true, "x") with { OriginatingUserRequest = "Compare the vendors" };
        var origin = new RockBot.UserProxy.ReplyOrigin("blazor", "first prompt of the session", DateTimeOffset.UtcNow, "s1");

        var (source, label) = SubagentResultHandler.ArchiveSourceAndLabel("s1", [result], origin);
        Assert.AreEqual(SavedResponseSources.Subagent, source);
        Assert.AreEqual("Compare the vendors", label);

        var (_, fallback) = SubagentResultHandler.ArchiveSourceAndLabel("s1", [MakeResult("b2", true, "x")], origin);
        Assert.AreEqual("first prompt of the session", fallback);
    }
}

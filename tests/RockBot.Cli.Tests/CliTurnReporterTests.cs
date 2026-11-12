using RockBot.UserProxy;
using RockBot.UserProxy.Cli;

namespace RockBot.Cli.Tests;

[TestClass]
public sealed class CliTurnReporterTests
{
    private static AgentReply Progress(string content, ToolCallInfo? call = null) => new()
    {
        Content = content, SessionId = "s1", AgentName = "RockBot", IsFinal = false, ToolCall = call,
    };

    private static TaskListItem Item(string id, string description, string status) =>
        new() { Id = id, Description = description, Status = status };

    [TestMethod]
    public void Progress_WithoutAToolCall_IsTheTextAsBefore()
    {
        Assert.AreEqual("[RockBot] Working on it", CliTurnReporter.FormatProgress(Progress("Working on it")));
    }

    [TestMethod]
    public void Progress_ToolCall_ShowsDurationAndFailure()
    {
        var started = new ToolCallInfo
        {
            ToolCallId = "c1", ToolCallName = "web_search", Phase = ToolCallPhase.Started, ArgsSummary = "query: rockbot",
        };
        Assert.AreEqual("[RockBot] → web_search: query: rockbot…",
            CliTurnReporter.FormatProgress(Progress("Calling web_search…", started)));

        var finished = started with { Phase = ToolCallPhase.Finished, Result = "3 results", DurationMs = 1250 };
        Assert.AreEqual("[RockBot] ✓ web_search → 3 results (1.3 s)",
            CliTurnReporter.FormatProgress(Progress("web_search → 3 results", finished)));

        var failed = started with { Phase = ToolCallPhase.Failed, Error = "timeout", DurationMs = 90 };
        Assert.AreEqual("[RockBot] ✗ web_search FAILED (90 ms): timeout",
            CliTurnReporter.FormatProgress(Progress("web_search failed", failed)));
    }

    [TestMethod]
    public void TaskList_PrintsACompactLineOnSnapshotAndDelta()
    {
        var lines = new List<string>();
        var reporter = new CliTurnReporter(lines.Add);

        reporter.OnTaskListSnapshot(new TaskListSnapshot
        {
            ThreadId = "s1", Source = ReplySource.Primary,
            Items = [Item("1", "Search", "pending"), Item("2", "Summarize", "pending")],
        });
        reporter.OnTaskListDelta(new TaskListDelta
        {
            ThreadId = "s1", Source = ReplySource.Primary, Item = Item("1", "Search", "completed"),
        });
        reporter.OnTaskListDelta(new TaskListDelta
        {
            ThreadId = "s1", Source = ReplySource.Primary, Item = Item("2", "Summarize", "completed"),
        });

        CollectionAssert.AreEqual(
            new[] { "[0/2] Search", "[1/2] Summarize", "[2/2] all done" },
            lines);
        Assert.AreEqual("[2/2] all done", reporter.TaskLine);
    }

    [TestMethod]
    public void Status_FollowsHeartbeatsAndProgress()
    {
        var reporter = new CliTurnReporter();
        Assert.IsNull(reporter.Status);

        reporter.OnRunHeartbeat(new RunHeartbeat
        {
            ThreadId = "s1", RunId = "r1", Timestamp = DateTimeOffset.UtcNow, ElapsedSeconds = 15, Status = "Calling web_search",
        });
        Assert.AreEqual("Calling web_search", reporter.Status);

        reporter.Report(Progress("Reading <b>3</b> pages"));
        Assert.AreEqual("Reading 3 pages", reporter.Status);
    }

    [TestMethod]
    [DataRow(TurnResultOutcome.Success, 0)]
    [DataRow(TurnResultOutcome.TimedOut, 2)]
    [DataRow(TurnResultOutcome.Cancelled, 3)]
    [DataRow(TurnResultOutcome.Error, 4)]
    [DataRow(TurnResultOutcome.AgentLost, 5)]
    public void ExitCodes_PerOutcome(TurnResultOutcome outcome, int expected)
    {
        Assert.AreEqual(expected, ChatExitCodes.For(outcome));
    }
}

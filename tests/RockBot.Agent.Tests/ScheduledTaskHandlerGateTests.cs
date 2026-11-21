using RockBot.Host;

namespace RockBot.Agent.Tests;

/// <summary>#848: how a passed gate's output is handed to the patrol turn.</summary>
[TestClass]
public sealed class ScheduledTaskHandlerGateTests
{
    private static readonly TaskGate Gate = new("mail", "new-ticket-mail.json");

    [TestMethod]
    public void WorkerInputKey_MapsAWispOutputKey_AndLeavesOthers()
    {
        Assert.AreEqual("wisp:w1/search", ScheduledTaskHandler.WorkerInputKey("wisp/w1/search/output"));
        Assert.AreEqual("notes/x", ScheduledTaskHandler.WorkerInputKey("notes/x"));
    }

    [TestMethod]
    public void FormatGateOutput_NamesTheWisp_TheWindow_AndTheKey()
    {
        var text = ScheduledTaskHandler.FormatGateOutput(Gate, """[{"id":"m1"}]""", "wisp/w1/search/output",
            new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));

        StringAssert.Contains(text, "(mail/new-ticket-mail.json)");
        StringAssert.Contains(text, "since the last run (2026-11-09 09:00 UTC)");
        StringAssert.Contains(text, "do not repeat those fetches");
        StringAssert.Contains(text, "`wisp/w1/search/output`");
        StringAssert.Contains(text, "`wisp:w1/search`");
        StringAssert.Contains(text, """[{"id":"m1"}]""");
    }

    [TestMethod]
    public void FormatGateOutput_TruncatesLongOutput_AndCopesWithoutKeyOrLastRun()
    {
        var output = new string('x', ScheduledTaskHandler.MaxGateOutputChars + 500);

        var text = ScheduledTaskHandler.FormatGateOutput(Gate, output, null, null);

        StringAssert.Contains(text, "(truncated;");
        Assert.IsFalse(text.Contains(new string('x', ScheduledTaskHandler.MaxGateOutputChars + 1)));
        Assert.IsFalse(text.Contains("working memory"));
        Assert.IsFalse(text.Contains("since the last run"));
    }
}

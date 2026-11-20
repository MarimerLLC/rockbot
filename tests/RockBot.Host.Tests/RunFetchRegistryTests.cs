using System.Text.Json;

namespace RockBot.Host.Tests;

/// <summary>#847: the run-scoped record of read-only fetches.</summary>
[TestClass]
public class RunFetchRegistryTests
{
    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [TestMethod]
    public void FirstCall_IsRecorded_RepeatIsFound()
    {
        var registry = new RunFetchRegistry();

        Assert.IsNull(registry.Observe("mail__search_emails", Args(("query", "x")), true, "by A", "k/1"));
        var prior = registry.Observe("mail__search_emails", Args(("query", "x")), true, "by B");

        Assert.IsNotNull(prior);
        Assert.AreEqual("by A", prior.Location, "The first location is kept.");
        Assert.AreEqual("k/1", prior.Key);
        Assert.AreEqual(1, registry.Count);
    }

    [TestMethod]
    public void ArgumentKeyOrder_DoesNotMatter()
    {
        var registry = new RunFetchRegistry();
        registry.Observe("mail__search_emails", """{"query":"x","filter":{"b":2,"a":1}}""", true, "by A");

        Assert.IsNotNull(registry.Observe("mail__search_emails", """{"filter":{"a":1,"b":2},"query":"x"}""", true, "by B"));
    }

    [TestMethod]
    public void McpInvokeTool_MatchesTheTypedName()
    {
        var registry = new RunFetchRegistry();
        registry.Observe("mcp_invoke_tool",
            """{"server_name":"mail","tool_name":"search_emails","arguments":{"query":"x","count":50}}""",
            true, "by wisp w1 step s", "wisp/w1/s/output");

        var prior = registry.Observe("mail__search_emails",
            Args(("count", JsonSerializer.SerializeToElement(50)), ("query", "x")), true, "by direct");

        Assert.IsNotNull(prior);
        Assert.AreEqual("mail__search_emails", prior.Tool);
        Assert.AreEqual("wisp/w1/s/output", prior.Key);
    }

    [TestMethod]
    public void McpInvokeTool_StringifiedInnerArguments_MatchesTheTypedName()
    {
        var registry = new RunFetchRegistry();
        registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by A");

        Assert.IsNotNull(registry.Observe("mcp_invoke_tool",
            Args(("server_name", "mail"), ("tool_name", "search_emails"), ("arguments", """{"query":"x"}""")),
            true, "by B"));
    }

    [TestMethod]
    public void DifferentServerOrArguments_AreDifferentFetches()
    {
        var registry = new RunFetchRegistry();
        registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by A");

        Assert.IsNull(registry.Observe("other__search_emails", """{"query":"x"}""", true, "by B"));
        Assert.IsNull(registry.Observe("mail__search_emails", """{"query":"y"}""", true, "by C"));
    }

    [TestMethod]
    public void FailedCalls_AreIgnored()
    {
        var registry = new RunFetchRegistry();
        Assert.IsNull(registry.Observe("mail__search_emails", """{"query":"x"}""", false, "by A"));
        Assert.IsNull(registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by B"),
            "A failed call is not a fetch the run made.");
        Assert.IsNull(registry.Observe("mail__search_emails", """{"query":"x"}""", false, "by C"),
            "A failed repeat is not flagged.");
        Assert.AreEqual("by B", registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by D")?.Location);
    }

    [TestMethod]
    public void WriteOnAServer_ForgetsThatServersReads_Only()
    {
        var registry = new RunFetchRegistry();
        registry.Observe("calendar__get_calendar_events", """{"day":"mon"}""", true, "by A");
        registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by A");

        Assert.IsNull(registry.Observe("calendar__create_event", """{"subject":"s"}""", true, "by W"),
            "A write is never a duplicate.");

        Assert.IsNull(registry.Observe("calendar__get_calendar_events", """{"day":"mon"}""", true, "by B"),
            "A re-read after a write on the same server is legitimate.");
        Assert.IsNotNull(registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by B"),
            "Another server's reads are kept.");
    }

    [TestMethod]
    [DataRow("get_from_working_memory")]
    [DataRow("search_working_memory")]
    [DataRow("task_update")]
    [DataRow("report_progress")]
    [DataRow("spawn_wisps")]
    [DataRow("spawn_workers")]
    [DataRow("spawn_subagent")]
    [DataRow("get_tool_guide")]
    public void BookkeepingAndBatchTools_AreNotTracked(string tool)
    {
        var registry = new RunFetchRegistry();
        registry.Observe(tool, """{"key":"x"}""", true, "by A");

        Assert.IsNull(registry.Observe(tool, """{"key":"x"}""", true, "by B"));
        Assert.AreEqual(0, registry.Count);
    }

    [TestMethod]
    public void McpInvokeToolWithoutTarget_IsNotTracked()
    {
        var registry = new RunFetchRegistry();
        registry.Observe("mcp_invoke_tool", """{"arguments":{}}""", true, "by A");
        Assert.AreEqual(0, registry.Count);
    }

    [TestMethod]
    public void Note_NamesTheKeyAndTheWorkerHandOff()
    {
        var note = RunFetchRegistry.FormatDuplicateNote(
            new RunFetchRegistry.Entry("mail__search_emails", "by wisp w1 step s", "wisp/w1/s/output"));

        StringAssert.Contains(note, "mail__search_emails");
        StringAssert.Contains(note, "by wisp w1 step s");
        StringAssert.Contains(note, "`wisp/w1/s/output`");
        StringAssert.Contains(note, "input_keys");
        StringAssert.Contains(note, "Do not fetch the same data again");
    }

    [TestMethod]
    public void ObserveLoopCall_OutsideALoop_DoesNothing()
    {
        using var _ = RunFetchRegistryContext.Set(null);
        Assert.IsNull(RunFetchRegistryContext.ObserveLoopCall("mail__search_emails", Args(("query", "x")), true));
        Assert.IsNull(RunFetchRegistryContext.ObserveLoopCall("mail__search_emails", Args(("query", "x")), true));
    }

    [TestMethod]
    public void ObserveLoopCall_SecondCall_ReturnsTheNote()
    {
        using var _ = RunFetchRegistryContext.Set(new RunFetchRegistry());
        Assert.IsNull(RunFetchRegistryContext.ObserveLoopCall("mail__search_emails", Args(("query", "x")), true));
        StringAssert.Contains(
            RunFetchRegistryContext.ObserveLoopCall("mail__search_emails", Args(("query", "x")), true)!,
            "already succeeded in this run");
    }
}

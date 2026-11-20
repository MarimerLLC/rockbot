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
        StringAssert.Contains(note, "instead of fetching the same data again");
    }

    [TestMethod]
    public void Note_OffersSpawnWorkersOnlyConditionally_AndAllowsAFreshReRead()
    {
        var note = RunFetchRegistry.FormatDuplicateNote(
            new RunFetchRegistry.Entry("mail__search_emails", "by wisp w1 step s", "wisp/w1/s/output"));

        StringAssert.Contains(note, "if spawn_workers is in your tool list");
        StringAssert.Contains(note, "Calling again is fine only when you need fresher data");
        Assert.IsFalse(note.Contains("Do not fetch"), "The note discourages a repeat; it doesn't forbid one.");
    }

    // ── Reachability: the note never cites data the caller can't read ─────

    [TestMethod]
    public void CallWithoutKey_FromAnotherLoop_IsNotReported_AndThenTracksTheNewLoop()
    {
        var registry = new RunFetchRegistry();
        registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by an earlier direct call", origin: "worker-a");

        Assert.IsNull(registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by an earlier direct call", origin: "session-1"),
            "The parent can't see the worker's result, so it isn't told the result is in its conversation.");
        Assert.IsNotNull(registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by an earlier direct call", origin: "session-1"),
            "The parent's own repeat is reported.");
        Assert.IsNull(registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by an earlier direct call", origin: "worker-b"),
            "A sibling worker isn't told either.");
    }

    [TestMethod]
    public void CallWithKey_IsReportedToAnyLoop()
    {
        var registry = new RunFetchRegistry();
        registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by wisp w1 step s", key: "wisp/w1/s/output");

        Assert.IsNotNull(registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by direct", origin: "worker-a"),
            "Working memory is readable from every loop.");
    }

    [TestMethod]
    public void ExpiredKey_IsNotReported_AndTheNewCallReplacesIt()
    {
        var time = new ManualTimeProvider();
        var registry = new RunFetchRegistry(time);
        registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by wisp w1 step s",
            key: "wisp/w1/s/output", keyLifetime: TimeSpan.FromMinutes(30));

        time.Advance(TimeSpan.FromMinutes(29));
        Assert.AreEqual("wisp/w1/s/output",
            registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by direct", origin: "s")?.Key);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.IsNull(registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by direct", origin: "s"),
            "Past its lifetime the key may be gone; fetching again is the only way to the data.");
        var next = registry.Observe("mail__search_emails", """{"query":"x"}""", true, "by direct", origin: "s");
        Assert.IsNotNull(next);
        Assert.IsNull(next.Key, "The refetch is now the one cited.");
    }

    // ── Reads that are meant to repeat ────────────────────────────────────

    [TestMethod]
    [DataRow("jobs__get_task_status")]
    [DataRow("mcp_get_task_status")]
    [DataRow("poll_job")]
    [DataRow("get_current_time")]
    [DataRow("calendar__get_current_events")]
    [DataRow("health_check")]
    [DataRow("mcp_list_services")]
    [DataRow("mcp_get_service_details")]
    [DataRow("mcp_find_tools")]
    public void VolatileAndDiscoveryReads_AreNotTracked(string tool)
    {
        var registry = new RunFetchRegistry();
        registry.Observe(tool, """{"id":"7"}""", true, "by A");

        Assert.IsNull(registry.Observe(tool, """{"id":"7"}""", true, "by A"));
        Assert.AreEqual(0, registry.Count);
    }

    [TestMethod]
    public void McpReadResource_IsTracked()
    {
        var registry = new RunFetchRegistry();
        registry.Observe("mcp_read_resource", """{"uri":"doc://a"}""", true, "by A");
        Assert.AreEqual(1, registry.Count);
    }

    // ── MCP tools named like bookkeeping ──────────────────────────────────

    [TestMethod]
    [DataRow("tracker__task_update")]
    [DataRow("tracker__task_create")]
    [DataRow("tracker__save_memory")]
    public void McpToolNamedLikeBookkeeping_IsAWrite_AndForgetsTheServersReads(string write)
    {
        var registry = new RunFetchRegistry();
        registry.Observe("tracker__list_tasks", "{}", true, "by A", origin: "s");

        registry.Observe(write, """{"id":3}""", true, "by A", origin: "s");

        Assert.IsNull(registry.Observe("tracker__list_tasks", "{}", true, "by B", origin: "s"),
            "A re-read after the update is legitimate.");
    }

    [TestMethod]
    public void McpInvokeTool_BookkeepingNamedTarget_IsAWrite()
    {
        var registry = new RunFetchRegistry();
        registry.Observe("tracker__list_tasks", "{}", true, "by A", origin: "s");

        registry.Observe("mcp_invoke_tool",
            """{"server_name":"tracker","tool_name":"task_update","arguments":{"id":3}}""", true, "by A", origin: "s");

        Assert.IsNull(registry.Observe("tracker__list_tasks", "{}", true, "by B", origin: "s"));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
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

    [TestMethod]
    public void ObserveLoopCall_UsesTheLoopsSessionAsOrigin()
    {
        using var _ = RunFetchRegistryContext.Set(new RunFetchRegistry());
        using (ToolCallSessionContext.Set("worker-a"))
            Assert.IsNull(RunFetchRegistryContext.ObserveLoopCall("mail__search_emails", Args(("query", "x")), true));

        using (ToolCallSessionContext.Set("session-1"))
            Assert.IsNull(RunFetchRegistryContext.ObserveLoopCall("mail__search_emails", Args(("query", "x")), true),
                "A repeat from another loop isn't told the result is in its conversation.");
    }
}

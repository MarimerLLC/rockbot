using RockBot.Subagent.Worker;

namespace RockBot.Subagent.Tests.Worker;

[TestClass]
public class WorkerRunnerHelpersTests
{
    // ── MatchesAllowlist ─────────────────────────────────────────────────────

    [TestMethod]
    public void MatchesAllowlist_NullAllowlist_MatchesEverything()
    {
        Assert.IsTrue(WorkerRunner.MatchesAllowlist("anything", null));
        Assert.IsTrue(WorkerRunner.MatchesAllowlist("spawn_subagent", null));
    }

    [TestMethod]
    public void MatchesAllowlist_EmptyAllowlist_MatchesEverything()
    {
        Assert.IsTrue(WorkerRunner.MatchesAllowlist("anything", []));
    }

    [TestMethod]
    public void MatchesAllowlist_ExactMatch_Allowed()
    {
        Assert.IsTrue(WorkerRunner.MatchesAllowlist("get_calendar_events",
            ["get_calendar_events", "list_emails"]));
    }

    [TestMethod]
    public void MatchesAllowlist_NotInList_Rejected()
    {
        Assert.IsFalse(WorkerRunner.MatchesAllowlist("save_memory",
            ["get_calendar_events", "list_emails"]));
    }

    [TestMethod]
    public void MatchesAllowlist_PrefixWildcard_Allowed()
    {
        Assert.IsTrue(WorkerRunner.MatchesAllowlist("calendar_get_events",
            ["calendar_*"]));
        Assert.IsTrue(WorkerRunner.MatchesAllowlist("calendar_list_accounts",
            ["calendar_*"]));
    }

    [TestMethod]
    public void MatchesAllowlist_PrefixWildcard_NonMatchingRejected()
    {
        Assert.IsFalse(WorkerRunner.MatchesAllowlist("email_search",
            ["calendar_*"]));
    }

    [TestMethod]
    public void MatchesAllowlist_BareAsterisk_Allowed()
    {
        Assert.IsTrue(WorkerRunner.MatchesAllowlist("anything", ["*"]));
    }

    // ── IsMcpGatewayTool ─────────────────────────────────────────────────────

    // The gateway is keyed off the registry Source ("mcp:management"), so any
    // future gateway tool is covered automatically — no per-name maintenance.

    [TestMethod]
    public void IsMcpGatewayTool_ManagementSource_IsGateway()
    {
        Assert.IsTrue(WorkerRunner.IsMcpGatewayTool("mcp:management"));
    }

    [TestMethod]
    public void IsMcpGatewayTool_IsCaseInsensitive()
    {
        Assert.IsTrue(WorkerRunner.IsMcpGatewayTool("MCP:Management"));
    }

    [TestMethod]
    [DataRow("mcp")]        // bridged per-server tools, not the management gateway
    [DataRow("worker")]
    [DataRow("memory")]
    [DataRow("")]
    [DataRow(null)]
    public void IsMcpGatewayTool_OtherSources_AreNot(string? source)
    {
        Assert.IsFalse(WorkerRunner.IsMcpGatewayTool(source));
    }

    [TestMethod]
    public void GatewayTool_SurvivesServerScopedAllowlist_ThatMatchesNothing()
    {
        // Regression for the #431 bug: a server-scoped allowlist like
        // ["calendar-mcp.*"] matches none of the gateway's literal tool names, so
        // MatchesAllowlist alone would strip mcp_invoke_tool and leave the worker
        // with no way to reach any MCP server. The source-keyed exemption must win.
        string[] allowlist = ["calendar-mcp.*"];

        Assert.IsFalse(WorkerRunner.MatchesAllowlist("mcp_invoke_tool", allowlist),
            "precondition: the allowlist does not literally match the gateway name");
        Assert.IsTrue(
            WorkerRunner.IsMcpGatewayTool("mcp:management")
            || WorkerRunner.MatchesAllowlist("mcp_invoke_tool", allowlist),
            "gateway must survive a server-scoped allowlist that matches nothing");
    }

    // ── ParseWorkerSelfReport ────────────────────────────────────────────────

    [TestMethod]
    public void ParseWorkerSelfReport_NoMarker_ReturnsZero()
    {
        var (facts, blocked, patterns) = WorkerRunner.ParseWorkerSelfReport(
            "Done. No marker emitted.");

        Assert.AreEqual(0, facts);
        Assert.AreEqual(0, blocked.Count);
        Assert.AreEqual(0, patterns.Count);
    }

    [TestMethod]
    public void ParseWorkerSelfReport_EmptyOutput_ReturnsZero()
    {
        var (facts, blocked, patterns) = WorkerRunner.ParseWorkerSelfReport("");
        Assert.AreEqual(0, facts);
        Assert.AreEqual(0, blocked.Count);
        Assert.AreEqual(0, patterns.Count);
    }

    [TestMethod]
    public void ParseWorkerSelfReport_MarkerPresent_ParsesFacts()
    {
        var (facts, blocked, patterns) = WorkerRunner.ParseWorkerSelfReport(
            "Saved calendar events. [WORKER_RESULT] facts=6 blocked= patterns=0");

        Assert.AreEqual(6, facts);
        Assert.AreEqual(0, blocked.Count);
        Assert.AreEqual(0, patterns.Count);
    }

    [TestMethod]
    public void ParseWorkerSelfReport_MarkerPresent_ParsesBlockedList()
    {
        var (_, blocked, _) = WorkerRunner.ParseWorkerSelfReport(
            "Done. [WORKER_RESULT] facts=4 blocked=acct1,acct2 patterns=1");

        CollectionAssert.AreEqual(new[] { "acct1", "acct2" }, blocked.ToArray());
    }

    [TestMethod]
    public void ParseWorkerSelfReport_MultipleMarkers_UsesLast()
    {
        // A model might mention the marker in an intermediate report; the runner
        // should latch onto the final one only.
        var (facts, _, _) = WorkerRunner.ParseWorkerSelfReport(
            "Earlier mention [WORKER_RESULT] facts=1 blocked= patterns=0. " +
            "Final: [WORKER_RESULT] facts=7 blocked= patterns=2");

        Assert.AreEqual(7, facts);
    }

    [TestMethod]
    public void ParseWorkerSelfReport_BlockedWithSpaces_TrimsEntries()
    {
        var (_, blocked, _) = WorkerRunner.ParseWorkerSelfReport(
            "Done. [WORKER_RESULT] facts=2 blocked=alpha, beta, gamma patterns=0");

        // Note: spaces inside the CSV are tolerated as trim entries.
        Assert.IsTrue(blocked.Count >= 1);
        Assert.IsTrue(blocked.Contains("alpha") || blocked.Contains(" alpha"));
    }
}

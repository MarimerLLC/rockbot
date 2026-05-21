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

using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent.McpBridge.Watches;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>The standing watch rules' file and matching (#853), and the watch ledger's new fields.</summary>
[TestClass]
public class McpWatchRuleStoreTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"rockbot-watch-rules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [TestMethod]
    [DataRow("scheduler://events/*/status", "scheduler://events/JPQNX23W/status", true)]
    [DataRow("scheduler://events/*/status", "scheduler://events/JPQNX23W/votes", false)]
    [DataRow("scheduler://events/*/status", "scheduler://events/a/b/status", false)] // * stays within a segment
    [DataRow("scheduler://events/*/status", "scheduler://events//status", true)]
    [DataRow("scheduler://events/AB*", "scheduler://events/AB12", true)]
    [DataRow("fixture://watch/poll", "fixture://watch/poll", true)]
    [DataRow("fixture://watch/poll", "fixture://watch/polls", false)]
    [DataRow("a.b?c(d)", "a.b?c(d)", true)] // regex characters are literal
    [DataRow("a.b", "axb", false)]
    [DataRow("Scheduler://x", "scheduler://x", false)] // URIs compare as written
    [DataRow("scheduler://events/a*b*c/status", "scheduler://events/aXbYc/status", true)]
    [DataRow("scheduler://events/a*b*c/status", "scheduler://events/aXbY/status", false)]
    [DataRow("*", "", true)]
    public void APattern_MatchesLiterally_WithStarForPartOfASegment(string pattern, string uri, bool matches) =>
        Assert.AreEqual(matches, McpWatchRule.UriMatches(pattern, uri));

    [TestMethod]
    [Timeout(5_000, CooperativeCancellation = true)]
    public void APathologicalPattern_IsStillQuick()
    {
        // A user's pattern can't make every delivery on the server slow or fail.
        var pattern = "x://e/" + string.Concat(Enumerable.Repeat("a*", 200)) + "b";
        var uri = "x://e/" + new string('a', 5_000);
        Assert.IsFalse(McpWatchRule.UriMatches(pattern, uri));
    }

    [TestMethod]
    public void ACallCountsAsAChangesCause_OnlyWhileRunningOrJustEnded_AndNotForHours()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var calls = new McpRecentCalls(clock);
        var grace = TimeSpan.FromSeconds(10);

        var ended = calls.Start("session/s", "scheduler", "withdraw_slot");
        clock.Advance(TimeSpan.FromSeconds(1));
        calls.End(ended);
        Assert.AreSame(ended, calls.CauseOf("session/s", "Scheduler", clock.GetUtcNow(), grace));
        Assert.IsNull(calls.CauseOf("session/other", "scheduler", clock.GetUtcNow(), grace), "another session's call isn't");
        Assert.IsNull(calls.CauseOf("session/s", "scheduler", clock.GetUtcNow() + TimeSpan.FromSeconds(30), grace), "past the grace");

        // A call that runs on (parked on the user's answer, a background task) stops counting.
        var open = calls.Start("session/s", "other", "create_event");
        Assert.AreSame(open, calls.CauseOf("session/s", "other", clock.GetUtcNow() + TimeSpan.FromSeconds(30), grace));
        Assert.IsNull(calls.CauseOf("session/s", "other", clock.GetUtcNow() + TimeSpan.FromHours(1), grace));
    }

    [TestMethod]
    public async Task Rules_AreSetReplacedAndRemoved_AndSurviveARestart()
    {
        var path = Path.Combine(_dir, "watch-rules.json");
        var store = new McpWatchRuleStore(path, NullLogger.Instance);

        var (rule, replaced) = await store.SetAsync("scheduler", "scheduler://events/*/status", "Propose new times.");
        Assert.IsFalse(replaced);
        StringAssert.StartsWith(rule.RuleId, "r_");
        var (again, replacedNow) = await store.SetAsync("SCHEDULER", "scheduler://events/*/status", "Just tell me.");
        Assert.IsTrue(replacedNow, "the server's name ignores case");
        Assert.AreEqual(rule.RuleId, again.RuleId);
        await store.SetAsync("scheduler", "scheduler://events/AB12/status", "Finalize it.");

        var reopened = new McpWatchRuleStore(path, NullLogger.Instance);
        var matching = await reopened.MatchingAsync("scheduler", "scheduler://events/AB12/status");
        CollectionAssert.AreEquivalent(new[] { "Just tell me.", "Finalize it." }, matching.Select(r => r.Instructions).ToArray());
        Assert.AreEqual(0, (await reopened.MatchingAsync("other", "scheduler://events/AB12/status")).Count);

        Assert.IsNotNull(await reopened.RemoveAsync("scheduler", "scheduler://events/*/status"));
        Assert.IsNull(await reopened.RemoveAsync("scheduler", "scheduler://events/*/status"));
        Assert.AreEqual(1, (await new McpWatchRuleStore(path, NullLogger.Instance).ActiveAsync()).Count);
    }

    [TestMethod]
    public async Task AWatchLedger_FromBeforeInstructions_StillReads()
    {
        // The new fields are additive: an older file has none of them.
        var path = Path.Combine(_dir, "watches.json");
        await File.WriteAllTextAsync(path, """
            {"version":1,"entries":[{"watchId":"w_1","status":"active","sessionId":"session/s","server":"scheduler",
            "uri":"scheduler://events/A/status","createdAt":"2026-11-01T00:00:00+00:00","expiresAt":"2026-11-15T00:00:00+00:00"}]}
            """);

        var entry = (await new McpWatchLedger(path, NullLogger.Instance).ReadAllAsync()).Single();
        Assert.IsNull(entry.OnChange);
        Assert.IsNull(entry.LifetimeStartedAt);
        Assert.IsNull(entry.ExpiryWarnedAt);
        Assert.IsTrue(entry.IsActive);
    }
}

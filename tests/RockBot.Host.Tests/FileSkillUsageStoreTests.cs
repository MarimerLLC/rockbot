using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileSkillUsageStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-skill-usage-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Append / read round-trip ──────────────────────────────────────────────

    [TestMethod]
    public async Task Append_And_GetBySession_RoundTrips()
    {
        var store = CreateStore();
        var evt1 = MakeEvent("session-1", "plan-meeting");
        var evt2 = MakeEvent("session-1", "send-email");

        await store.AppendAsync(evt1);
        await store.AppendAsync(evt2);

        var results = await store.GetBySessionAsync("session-1");

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Any(e => e.Id == evt1.Id && e.SkillName == "plan-meeting"));
        Assert.IsTrue(results.Any(e => e.Id == evt2.Id && e.SkillName == "send-email"));
    }

    [TestMethod]
    public async Task GetBySession_NotFound_ReturnsEmpty()
    {
        var store = CreateStore();
        var results = await store.GetBySessionAsync("nonexistent-session");
        Assert.AreEqual(0, results.Count);
    }

    // ── QueryRecent filtering ─────────────────────────────────────────────────

    [TestMethod]
    public async Task QueryRecent_FiltersOldEvents()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        var old = MakeEvent("session-1", "old-skill", timestamp: now.AddDays(-35));
        var recent = MakeEvent("session-1", "recent-skill", timestamp: now.AddDays(-5));

        await store.AppendAsync(old);
        await store.AppendAsync(recent);

        var results = await store.QueryRecentAsync(since: now.AddDays(-30), maxResults: 100);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("recent-skill", results[0].SkillName);
    }

    [TestMethod]
    public async Task QueryRecent_AcrossMultipleSessions()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync(MakeEvent("session-A", "skill-x", timestamp: now.AddDays(-1)));
        await store.AppendAsync(MakeEvent("session-B", "skill-y", timestamp: now.AddDays(-2)));
        await store.AppendAsync(MakeEvent("session-C", "skill-z", timestamp: now.AddDays(-3)));

        var results = await store.QueryRecentAsync(since: now.AddDays(-30), maxResults: 100);

        Assert.AreEqual(3, results.Count);
        Assert.IsTrue(results.Any(e => e.SkillName == "skill-x" && e.SessionId == "session-A"));
        Assert.IsTrue(results.Any(e => e.SkillName == "skill-y" && e.SessionId == "session-B"));
        Assert.IsTrue(results.Any(e => e.SkillName == "skill-z" && e.SessionId == "session-C"));
    }

    [TestMethod]
    public async Task QueryRecent_RespectsMaxResults()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
            await store.AppendAsync(MakeEvent("session-1", $"skill-{i}", timestamp: now.AddMinutes(-i)));

        var results = await store.QueryRecentAsync(since: now.AddHours(-1), maxResults: 3);

        Assert.AreEqual(3, results.Count);
    }

    [TestMethod]
    public async Task QueryRecent_OrdersByTimestampAscending()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        var first = MakeEvent("s", "skill-first", timestamp: now.AddMinutes(-30));
        var second = MakeEvent("s", "skill-second", timestamp: now.AddMinutes(-20));
        var third = MakeEvent("s", "skill-third", timestamp: now.AddMinutes(-10));

        // Append out of order
        await store.AppendAsync(third);
        await store.AppendAsync(first);
        await store.AppendAsync(second);

        var results = await store.QueryRecentAsync(since: now.AddHours(-1), maxResults: 100);

        Assert.AreEqual(3, results.Count);
        Assert.AreEqual("skill-first", results[0].SkillName);
        Assert.AreEqual("skill-second", results[1].SkillName);
        Assert.AreEqual("skill-third", results[2].SkillName);
    }

    [TestMethod]
    public async Task QueryRecent_EmptyStore_ReturnsEmpty()
    {
        var store = CreateStore();
        var results = await store.QueryRecentAsync(since: DateTimeOffset.UtcNow.AddDays(-30), maxResults: 100);
        Assert.AreEqual(0, results.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private FileSkillUsageStore CreateStore()
    {
        var skillOptions = Options.Create(new SkillOptions
        {
            UsageBasePath = Path.Combine(_tempDir, "skill-usage")
        });
        var profileOptions = Options.Create(new AgentProfileOptions { BasePath = _tempDir });
        return new FileSkillUsageStore(skillOptions, profileOptions, NullLogger<FileSkillUsageStore>.Instance);
    }

    private static SkillInvocationEvent MakeEvent(
        string sessionId,
        string skillName,
        DateTimeOffset? timestamp = null)
    {
        return new SkillInvocationEvent(
            Id: Guid.NewGuid().ToString("N")[..12],
            SkillName: skillName,
            SessionId: sessionId,
            Timestamp: timestamp ?? DateTimeOffset.UtcNow);
    }
}

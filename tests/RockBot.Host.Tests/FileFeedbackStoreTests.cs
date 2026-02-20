using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileFeedbackStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-feedback-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Append / read round-trip ──────────────────────────────────────────────

    [TestMethod]
    public async Task AppendAsync_And_GetBySessionAsync_RoundTrips()
    {
        var store = CreateStore();
        var entry = MakeEntry("session-1", FeedbackSignalType.Correction, "user corrected agent");

        await store.AppendAsync(entry);
        var results = await store.GetBySessionAsync("session-1");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(entry.Id, results[0].Id);
        Assert.AreEqual("session-1", results[0].SessionId);
        Assert.AreEqual(FeedbackSignalType.Correction, results[0].SignalType);
        Assert.AreEqual("user corrected agent", results[0].Summary);
    }

    [TestMethod]
    public async Task AppendAsync_MultipleEntries_AllPersisted()
    {
        var store = CreateStore();
        var e1 = MakeEntry("session-1", FeedbackSignalType.Correction, "correction 1");
        var e2 = MakeEntry("session-1", FeedbackSignalType.ToolFailure, "tool error");
        var e3 = MakeEntry("session-1", FeedbackSignalType.SessionSummary, "session evaluated");

        await store.AppendAsync(e1);
        await store.AppendAsync(e2);
        await store.AppendAsync(e3);

        var results = await store.GetBySessionAsync("session-1");
        Assert.AreEqual(3, results.Count);
    }

    // ── GetBySessionAsync isolation ───────────────────────────────────────────

    [TestMethod]
    public async Task GetBySessionAsync_ReturnsOnlyThatSession()
    {
        var store = CreateStore();
        await store.AppendAsync(MakeEntry("session-A", FeedbackSignalType.Correction, "for A"));
        await store.AppendAsync(MakeEntry("session-B", FeedbackSignalType.ToolFailure, "for B"));

        var resultsA = await store.GetBySessionAsync("session-A");
        var resultsB = await store.GetBySessionAsync("session-B");

        Assert.AreEqual(1, resultsA.Count);
        Assert.AreEqual("session-A", resultsA[0].SessionId);
        Assert.AreEqual(1, resultsB.Count);
        Assert.AreEqual("session-B", resultsB[0].SessionId);
    }

    [TestMethod]
    public async Task GetBySessionAsync_UnknownSession_ReturnsEmpty()
    {
        var store = CreateStore();
        var results = await store.GetBySessionAsync("nonexistent");
        Assert.AreEqual(0, results.Count);
    }

    // ── Multiple sessions in separate files ───────────────────────────────────

    [TestMethod]
    public async Task MultipleSessionsAreStoredInSeparateFiles()
    {
        var store = CreateStore();
        await store.AppendAsync(MakeEntry("sess-1", FeedbackSignalType.Correction, "a"));
        await store.AppendAsync(MakeEntry("sess-2", FeedbackSignalType.ToolFailure, "b"));

        var feedbackDir = Path.Combine(_tempDir, "feedback");
        var files = Directory.GetFiles(feedbackDir, "*.jsonl");
        Assert.AreEqual(2, files.Length);
        Assert.IsTrue(files.Any(f => Path.GetFileNameWithoutExtension(f) == "sess-1"));
        Assert.IsTrue(files.Any(f => Path.GetFileNameWithoutExtension(f) == "sess-2"));
    }

    // ── QueryRecentAsync ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task QueryRecentAsync_FiltersEntriesBeforeSince()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        var old = MakeEntry("session-1", FeedbackSignalType.Correction, "old", timestamp: now.AddHours(-2));
        var recent = MakeEntry("session-1", FeedbackSignalType.ToolFailure, "recent", timestamp: now.AddMinutes(-5));

        await store.AppendAsync(old);
        await store.AppendAsync(recent);

        var results = await store.QueryRecentAsync(since: now.AddHours(-1), maxResults: 100);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("recent", results[0].Summary);
    }

    [TestMethod]
    public async Task QueryRecentAsync_RespectsMaxResults()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
            await store.AppendAsync(MakeEntry("session-1", FeedbackSignalType.Correction, $"entry-{i}", timestamp: now.AddMinutes(-i)));

        var results = await store.QueryRecentAsync(since: now.AddHours(-1), maxResults: 3);

        Assert.AreEqual(3, results.Count);
    }

    [TestMethod]
    public async Task QueryRecentAsync_ScansAllSessions()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync(MakeEntry("session-A", FeedbackSignalType.Correction, "from A", timestamp: now.AddMinutes(-10)));
        await store.AppendAsync(MakeEntry("session-B", FeedbackSignalType.ToolFailure, "from B", timestamp: now.AddMinutes(-5)));

        var results = await store.QueryRecentAsync(since: now.AddHours(-1), maxResults: 100);

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Any(r => r.Summary == "from A"));
        Assert.IsTrue(results.Any(r => r.Summary == "from B"));
    }

    [TestMethod]
    public async Task QueryRecentAsync_EmptyStore_ReturnsEmpty()
    {
        var store = CreateStore();
        var results = await store.QueryRecentAsync(since: DateTimeOffset.UtcNow.AddDays(-1), maxResults: 100);
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task QueryRecentAsync_OrdersByTimestampAscending()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        var first = MakeEntry("s", FeedbackSignalType.Correction, "first", timestamp: now.AddMinutes(-30));
        var second = MakeEntry("s", FeedbackSignalType.ToolFailure, "second", timestamp: now.AddMinutes(-20));
        var third = MakeEntry("s", FeedbackSignalType.SessionSummary, "third", timestamp: now.AddMinutes(-10));

        // Append in non-chronological order
        await store.AppendAsync(third);
        await store.AppendAsync(first);
        await store.AppendAsync(second);

        var results = await store.QueryRecentAsync(since: now.AddHours(-1), maxResults: 100);

        Assert.AreEqual(3, results.Count);
        Assert.AreEqual("first", results[0].Summary);
        Assert.AreEqual("second", results[1].Summary);
        Assert.AreEqual("third", results[2].Summary);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private FileFeedbackStore CreateStore()
    {
        var feedbackOptions = Options.Create(new FeedbackOptions { BasePath = Path.Combine(_tempDir, "feedback") });
        var profileOptions = Options.Create(new AgentProfileOptions { BasePath = _tempDir });
        return new FileFeedbackStore(feedbackOptions, profileOptions, NullLogger<FileFeedbackStore>.Instance);
    }

    private static FeedbackEntry MakeEntry(
        string sessionId,
        FeedbackSignalType signalType,
        string summary,
        string? detail = null,
        DateTimeOffset? timestamp = null)
    {
        return new FeedbackEntry(
            Id: Guid.NewGuid().ToString("N")[..12],
            SessionId: sessionId,
            SignalType: signalType,
            Summary: summary,
            Detail: detail,
            Timestamp: timestamp ?? DateTimeOffset.UtcNow);
    }
}

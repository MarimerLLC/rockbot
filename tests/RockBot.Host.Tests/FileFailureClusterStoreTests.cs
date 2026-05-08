using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileFailureClusterStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-cluster-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task RecordAsync_FirstFailure_CreatesClusterWithCountOne()
    {
        var store = CreateStore();
        var key = new ClusterKey("calendar-mcp", "get_calendar_events", "timeZone");

        await store.RecordAsync(key, "session-1", "timeZone is required", DateTimeOffset.UtcNow);

        var all = await store.GetAllAsync();
        Assert.AreEqual(1, all.Count);
        var cluster = all[0];
        Assert.AreEqual(key, cluster.Key);
        Assert.AreEqual(1, cluster.Count);
        CollectionAssert.AreEquivalent(new[] { "session-1" }, cluster.SessionIds.ToArray());
        Assert.AreEqual(1, cluster.SampleErrorMessages.Count);
        Assert.AreEqual("timeZone is required", cluster.SampleErrorMessages[0]);
    }

    [TestMethod]
    public async Task GetEscalatable_ThreeFailuresAcrossTwoSessions_InWindow_ReturnsCluster()
    {
        var store = CreateStore();
        var key = new ClusterKey("calendar-mcp", "get_calendar_events", "timeZone");
        var now = DateTimeOffset.UtcNow;

        await store.RecordAsync(key, "session-1", "timeZone is required", now);
        await store.RecordAsync(key, "session-1", "timeZone is required", now);
        await store.RecordAsync(key, "session-2", "timeZone is required", now);

        var escalatable = await store.GetEscalatableAsync(now);
        Assert.AreEqual(1, escalatable.Count);
        Assert.AreEqual(3, escalatable[0].Count);
        Assert.AreEqual(2, escalatable[0].SessionIds.Count);
    }

    [TestMethod]
    public async Task GetEscalatable_BelowCountThreshold_ReturnsEmpty()
    {
        var store = CreateStore();
        var key = new ClusterKey("calendar-mcp", "get_calendar_events", "timeZone");
        var now = DateTimeOffset.UtcNow;

        await store.RecordAsync(key, "session-1", "err", now);
        await store.RecordAsync(key, "session-2", "err", now);

        var escalatable = await store.GetEscalatableAsync(now);
        Assert.AreEqual(0, escalatable.Count);
    }

    [TestMethod]
    public async Task GetEscalatable_OnlyOneSession_ReturnsEmpty()
    {
        var store = CreateStore();
        var key = new ClusterKey("calendar-mcp", "get_calendar_events", "timeZone");
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 5; i++)
            await store.RecordAsync(key, "session-1", "err", now);

        var escalatable = await store.GetEscalatableAsync(now);
        Assert.AreEqual(0, escalatable.Count);
    }

    [TestMethod]
    public async Task GetEscalatable_OutsideRecencyWindow_ReturnsEmpty()
    {
        var store = CreateStore();
        var key = new ClusterKey("calendar-mcp", "get_calendar_events", "timeZone");
        var twoDaysAgo = DateTimeOffset.UtcNow.AddDays(-2);

        await store.RecordAsync(key, "session-1", "err", twoDaysAgo);
        await store.RecordAsync(key, "session-1", "err", twoDaysAgo);
        await store.RecordAsync(key, "session-2", "err", twoDaysAgo);

        var escalatable = await store.GetEscalatableAsync(DateTimeOffset.UtcNow);
        Assert.AreEqual(0, escalatable.Count);
    }

    [TestMethod]
    public async Task RecordAsync_BoundsSampleMessagesToFiveMostRecent()
    {
        var store = CreateStore();
        var key = new ClusterKey("svr", "tool", "field");
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 8; i++)
            await store.RecordAsync(key, "s", $"error-{i}", now);

        var cluster = (await store.GetAllAsync())[0];
        Assert.AreEqual(8, cluster.Count);
        Assert.AreEqual(5, cluster.SampleErrorMessages.Count);
        // Most-recent-kept order: errors 3..7
        CollectionAssert.AreEqual(
            new[] { "error-3", "error-4", "error-5", "error-6", "error-7" },
            cluster.SampleErrorMessages.ToArray());
    }

    [TestMethod]
    public async Task RecordAsync_TruncatesLongErrorMessage()
    {
        var store = CreateStore();
        var key = new ClusterKey("svr", "tool", "field");
        var longMessage = new string('x', 1024);

        await store.RecordAsync(key, "s", longMessage, DateTimeOffset.UtcNow);

        var sample = (await store.GetAllAsync())[0].SampleErrorMessages[0];
        Assert.IsTrue(sample.Length <= 513, $"sample length was {sample.Length}");
        Assert.IsTrue(sample.EndsWith("…"));
    }

    [TestMethod]
    public async Task RecordAsync_NullSessionIdDoesNotPolluteSessionSet()
    {
        var store = CreateStore();
        var key = new ClusterKey("svr", "tool", "field");
        var now = DateTimeOffset.UtcNow;

        await store.RecordAsync(key, sessionId: null, "err", now);
        await store.RecordAsync(key, sessionId: null, "err", now);

        var cluster = (await store.GetAllAsync())[0];
        Assert.AreEqual(2, cluster.Count);
        Assert.AreEqual(0, cluster.SessionIds.Count);
    }

    [TestMethod]
    public async Task Persistence_SnapshotAndJsonl_RestoreClusterState()
    {
        var key = new ClusterKey("svr", "tool", "field");
        var t0 = DateTimeOffset.UtcNow;

        // First instance: record + flush.
        var first = CreateStore();
        await first.RecordAsync(key, "session-1", "err1", t0);
        await first.RecordAsync(key, "session-2", "err2", t0);
        await first.FlushAsync(CancellationToken.None);

        // Record another after flush — lands in JSONL only. Real callers pass
        // DateTimeOffset.UtcNow at the moment of recording, which is always after
        // the snapshot's WrittenAt; mirror that here.
        await first.RecordAsync(key, "session-3", "err3", DateTimeOffset.UtcNow.AddSeconds(1));

        // Second instance: should load both snapshot + JSONL replay.
        var second = CreateStore();
        await second.StartAsync(CancellationToken.None);

        var clusters = await second.GetAllAsync();
        Assert.AreEqual(1, clusters.Count);
        Assert.AreEqual(3, clusters[0].Count);
        CollectionAssert.AreEquivalent(
            new[] { "session-1", "session-2", "session-3" },
            clusters[0].SessionIds.ToArray());

        await second.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Flush_TruncatesJsonl()
    {
        var key = new ClusterKey("svr", "tool", "field");

        var store = CreateStore();
        await store.RecordAsync(key, "s1", "err", DateTimeOffset.UtcNow);
        await store.RecordAsync(key, "s2", "err", DateTimeOffset.UtcNow);

        var jsonlPath = Path.Combine(_tempDir, "failure-clusters.jsonl");
        Assert.IsTrue(new FileInfo(jsonlPath).Length > 0, "jsonl should have content before flush");

        await store.FlushAsync(CancellationToken.None);

        Assert.AreEqual(0, new FileInfo(jsonlPath).Length, "jsonl should be truncated after flush");

        var snapshotPath = Path.Combine(_tempDir, "failure-clusters.snapshot.json");
        Assert.IsTrue(File.Exists(snapshotPath), "snapshot should exist after flush");
        Assert.IsTrue(new FileInfo(snapshotPath).Length > 0, "snapshot should have content");
    }

    [TestMethod]
    public async Task Persistence_CorruptJsonlLine_IsSkipped()
    {
        var key = new ClusterKey("svr", "tool", "field");
        var t0 = DateTimeOffset.UtcNow;

        var first = CreateStore();
        await first.RecordAsync(key, "session-1", "err1", t0);
        await first.FlushAsync(CancellationToken.None);
        await first.RecordAsync(key, "session-2", "err2", DateTimeOffset.UtcNow.AddSeconds(1));

        // Append a malformed line that should be ignored on replay.
        var jsonlPath = Path.Combine(_tempDir, "failure-clusters.jsonl");
        await File.AppendAllTextAsync(jsonlPath, "not-valid-json{{{" + Environment.NewLine);

        var second = CreateStore();
        await second.StartAsync(CancellationToken.None);

        var clusters = await second.GetAllAsync();
        Assert.AreEqual(1, clusters.Count);
        Assert.AreEqual(2, clusters[0].Count);

        await second.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Persistence_JsonlEventsBeforeSnapshotTime_AreNotDoubleApplied()
    {
        var key = new ClusterKey("svr", "tool", "field");

        var first = CreateStore();
        // Record an old event (snapshot included it).
        await first.RecordAsync(key, "session-1", "err1", DateTimeOffset.UtcNow.AddMinutes(-5));
        await first.FlushAsync(CancellationToken.None);
        // Old JSONL is truncated after flush. Now record a new event.
        await first.RecordAsync(key, "session-2", "err2", DateTimeOffset.UtcNow);

        var second = CreateStore();
        await second.StartAsync(CancellationToken.None);

        var clusters = await second.GetAllAsync();
        // Snapshot has 1 (session-1), JSONL has 1 (session-2). Total = 2 (not 3).
        Assert.AreEqual(2, clusters[0].Count);

        await second.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task GetAllAsync_OrdersByLastSeenDescending()
    {
        var store = CreateStore();
        var older = new ClusterKey("svr", "tool", "older");
        var newer = new ClusterKey("svr", "tool", "newer");
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var t2 = DateTimeOffset.UtcNow;

        await store.RecordAsync(older, "s", "e", t1);
        await store.RecordAsync(newer, "s", "e", t2);

        var all = await store.GetAllAsync();
        Assert.AreEqual(2, all.Count);
        Assert.AreEqual(newer, all[0].Key);
        Assert.AreEqual(older, all[1].Key);
    }

    private FileFailureClusterStore CreateStore() => new(
        Options.Create(new FailureClusterOptions
        {
            BasePath = _tempDir,
            // Disable timer-based flush for tests; we drive it explicitly.
            FlushInterval = TimeSpan.Zero,
        }),
        Options.Create(new AgentProfileOptions()),
        NullLogger<FileFailureClusterStore>.Instance);
}

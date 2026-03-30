using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileToolCallLogTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-toolcall-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task AppendAsync_And_GetBySessionAsync_RoundTrips()
    {
        var log = CreateLog();
        var evt = MakeEvent("session-1", "search_emails", succeeded: true);

        await log.AppendAsync(evt);
        var results = await log.GetBySessionAsync("session-1");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("search_emails", results[0].ToolName);
        Assert.AreEqual("session-1", results[0].SessionId);
        Assert.IsTrue(results[0].Succeeded);
    }

    [TestMethod]
    public async Task GetBySessionAsync_UnknownSession_ReturnsEmpty()
    {
        var log = CreateLog();

        var results = await log.GetBySessionAsync("nonexistent");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task AppendAsync_MultipleSessions_Isolated()
    {
        var log = CreateLog();
        await log.AppendAsync(MakeEvent("s1", "tool_a"));
        await log.AppendAsync(MakeEvent("s1", "tool_b"));
        await log.AppendAsync(MakeEvent("s2", "tool_c"));

        var s1 = await log.GetBySessionAsync("s1");
        var s2 = await log.GetBySessionAsync("s2");

        Assert.AreEqual(2, s1.Count);
        Assert.AreEqual(1, s2.Count);
        Assert.AreEqual("tool_c", s2[0].ToolName);
    }

    [TestMethod]
    public async Task QueryRecentAsync_FiltersOldEvents()
    {
        var log = CreateLog();
        var old = MakeEvent("s1", "old_tool", timestamp: DateTimeOffset.UtcNow.AddDays(-30));
        var recent = MakeEvent("s1", "new_tool", timestamp: DateTimeOffset.UtcNow);

        await log.AppendAsync(old);
        await log.AppendAsync(recent);

        var results = await log.QueryRecentAsync(DateTimeOffset.UtcNow.AddDays(-7), maxResults: 100);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("new_tool", results[0].ToolName);
    }

    [TestMethod]
    public async Task QueryRecentAsync_RespectsMaxResults()
    {
        var log = CreateLog();
        for (var i = 0; i < 10; i++)
            await log.AppendAsync(MakeEvent("s1", $"tool_{i}"));

        var results = await log.QueryRecentAsync(DateTimeOffset.MinValue, maxResults: 3);

        Assert.AreEqual(3, results.Count);
    }

    [TestMethod]
    public async Task QueryRecentAsync_OrdersByTimestamp()
    {
        var log = CreateLog();
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-5);
        var t3 = DateTimeOffset.UtcNow;

        await log.AppendAsync(MakeEvent("s1", "third", timestamp: t3));
        await log.AppendAsync(MakeEvent("s1", "first", timestamp: t1));
        await log.AppendAsync(MakeEvent("s1", "second", timestamp: t2));

        var results = await log.QueryRecentAsync(DateTimeOffset.MinValue, maxResults: 100);

        Assert.AreEqual("first", results[0].ToolName);
        Assert.AreEqual("second", results[1].ToolName);
        Assert.AreEqual("third", results[2].ToolName);
    }

    [TestMethod]
    public async Task QueryRecentAsync_EmptyStore_ReturnsEmpty()
    {
        var log = CreateLog();

        var results = await log.QueryRecentAsync(DateTimeOffset.MinValue, maxResults: 100);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task AppendAsync_PreservesArgumentsSummary()
    {
        var log = CreateLog();
        var evt = new ToolCallEvent("s1", "search_emails", "query=meeting, limit=10", true, 150, DateTimeOffset.UtcNow);

        await log.AppendAsync(evt);
        var results = await log.GetBySessionAsync("s1");

        Assert.AreEqual("query=meeting, limit=10", results[0].ArgumentsSummary);
        Assert.AreEqual(150, results[0].DurationMs);
    }

    [TestMethod]
    public async Task QueryRecentAsync_AcrossMultipleSessions()
    {
        var log = CreateLog();
        await log.AppendAsync(MakeEvent("s1", "tool_a"));
        await log.AppendAsync(MakeEvent("s2", "tool_b"));
        await log.AppendAsync(MakeEvent("s3", "tool_c"));

        var results = await log.QueryRecentAsync(DateTimeOffset.MinValue, maxResults: 100);

        Assert.AreEqual(3, results.Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private FileToolCallLog CreateLog() =>
        new(Options.Create(new ToolCallLogOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            NullLogger<FileToolCallLog>.Instance);

    private static ToolCallEvent MakeEvent(
        string sessionId,
        string toolName,
        bool succeeded = true,
        DateTimeOffset? timestamp = null) =>
        new(sessionId, toolName, null, succeeded, 100, timestamp ?? DateTimeOffset.UtcNow);
}

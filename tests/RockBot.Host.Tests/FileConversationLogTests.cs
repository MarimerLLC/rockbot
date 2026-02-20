using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileConversationLogTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-convlog-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Append / read round-trip ──────────────────────────────────────────────

    [TestMethod]
    public async Task AppendAsync_And_ReadAllAsync_RoundTrips()
    {
        var log = CreateLog();
        var e1 = MakeEntry("session-1", "user", "Hello, world!");
        var e2 = MakeEntry("session-1", "assistant", "Hi there!");

        await log.AppendAsync(e1);
        await log.AppendAsync(e2);

        var results = await log.ReadAllAsync();

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual(e1.SessionId, results[0].SessionId);
        Assert.AreEqual(e1.Role, results[0].Role);
        Assert.AreEqual(e1.Content, results[0].Content);
        Assert.AreEqual(e2.Role, results[1].Role);
        Assert.AreEqual(e2.Content, results[1].Content);
    }

    [TestMethod]
    public async Task AppendAsync_MultipleSessionsAndRoles_AllPersisted()
    {
        var log = CreateLog();

        await log.AppendAsync(MakeEntry("session-A", "user", "A user message"));
        await log.AppendAsync(MakeEntry("session-B", "user", "B user message"));
        await log.AppendAsync(MakeEntry("session-A", "assistant", "A assistant reply"));

        var results = await log.ReadAllAsync();
        Assert.AreEqual(3, results.Count);
    }

    // ── ClearAsync ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ClearAsync_EmptiesLog()
    {
        var log = CreateLog();

        await log.AppendAsync(MakeEntry("session-1", "user", "some content"));
        await log.AppendAsync(MakeEntry("session-1", "assistant", "some reply"));

        await log.ClearAsync();

        var results = await log.ReadAllAsync();
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task ClearAsync_WhenFileDoesNotExist_DoesNotThrow()
    {
        var log = CreateLog();

        // Should not throw even if no file exists
        await log.ClearAsync();
        var results = await log.ReadAllAsync();
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task AppendAsync_AfterClear_StartsFromEmpty()
    {
        var log = CreateLog();

        await log.AppendAsync(MakeEntry("session-1", "user", "old content"));
        await log.ClearAsync();
        await log.AppendAsync(MakeEntry("session-2", "user", "new content"));

        var results = await log.ReadAllAsync();
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("new content", results[0].Content);
    }

    // ── ReadAllAsync on missing file ──────────────────────────────────────────

    [TestMethod]
    public async Task ReadAllAsync_OnMissingFile_ReturnsEmpty()
    {
        var log = CreateLog();

        var results = await log.ReadAllAsync();

        Assert.AreEqual(0, results.Count);
    }

    // ── Concurrent appends ────────────────────────────────────────────────────

    [TestMethod]
    public async Task ConcurrentAppends_DoNotCorruptFile()
    {
        var log = CreateLog();
        const int count = 20;

        var tasks = Enumerable.Range(0, count)
            .Select(i => log.AppendAsync(MakeEntry("session-1", "user", $"message-{i}")))
            .ToList();

        await Task.WhenAll(tasks);

        var results = await log.ReadAllAsync();
        Assert.AreEqual(count, results.Count, "All concurrent appends should be persisted without corruption");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private FileConversationLog CreateLog()
    {
        var logOptions = Options.Create(new ConversationLogOptions
        {
            BasePath = Path.Combine(_tempDir, "conversation-log")
        });
        var profileOptions = Options.Create(new AgentProfileOptions { BasePath = _tempDir });
        return new FileConversationLog(logOptions, profileOptions, NullLogger<FileConversationLog>.Instance);
    }

    private static ConversationLogEntry MakeEntry(
        string sessionId,
        string role,
        string content,
        DateTimeOffset? timestamp = null)
    {
        return new ConversationLogEntry(
            SessionId: sessionId,
            Role: role,
            Content: content,
            Timestamp: timestamp ?? DateTimeOffset.UtcNow);
    }
}

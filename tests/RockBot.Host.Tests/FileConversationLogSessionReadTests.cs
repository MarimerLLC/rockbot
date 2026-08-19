using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers the session-scoped reads <c>FileConversationLog</c> adds over
/// <see cref="IConversationLog.ReadAllAsync"/>, which exist so a user-facing tool call does
/// not have to materialise the whole multi-session log.
/// </summary>
[TestClass]
public class FileConversationLogSessionReadTests
{
    private string _root = null!;
    private IConversationLog _log = null!;

    private static readonly DateTimeOffset Origin = new(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "rockbot-convlog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _log = new FileConversationLog(
            Options.Create(new ConversationLogOptions { BasePath = _root }),
            Options.Create(new AgentProfileOptions { BasePath = _root }),
            NullLogger<FileConversationLog>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private Task AppendAsync(string sessionId, string role, string content, int minuteOffset) =>
        _log.AppendAsync(new ConversationLogEntry(sessionId, role, content, Origin.AddMinutes(minuteOffset)));

    // ── ReadSessionAsync ──────────────────────────────────────────────────

    [TestMethod]
    public async Task ReadSessionAsync_NoFile_ReturnsEmpty()
    {
        var result = await _log.ReadSessionAsync("session/absent", 10);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task ReadSessionAsync_ReturnsOnlyTheRequestedSession()
    {
        await AppendAsync("session/a", "user", "alpha one", 0);
        await AppendAsync("session/b", "user", "bravo one", 1);
        await AppendAsync("session/a", "assistant", "alpha two", 2);

        var result = await _log.ReadSessionAsync("session/a", 10);

        Assert.AreEqual(2, result.Count);
        CollectionAssert.AreEqual(
            new[] { "alpha one", "alpha two" },
            result.Select(e => e.Content).ToArray());
    }

    [TestMethod]
    public async Task ReadSessionAsync_BeyondCap_KeepsTheMostRecent()
    {
        for (var i = 0; i < 10; i++)
            await AppendAsync("session/a", "user", $"entry {i}", i);

        var result = await _log.ReadSessionAsync("session/a", 3);

        Assert.AreEqual(3, result.Count);
        CollectionAssert.AreEqual(
            new[] { "entry 7", "entry 8", "entry 9" },
            result.Select(e => e.Content).ToArray(),
            "The bound must drop the oldest entries, not truncate at the head of the file.");
    }

    [TestMethod]
    public async Task ReadSessionAsync_ReturnsChronologicalOrder()
    {
        await _log.AppendAsync(new ConversationLogEntry("session/a", "user", "second", Origin.AddMinutes(5)));
        await _log.AppendAsync(new ConversationLogEntry("session/a", "user", "first", Origin));

        var result = await _log.ReadSessionAsync("session/a", 10);

        CollectionAssert.AreEqual(
            new[] { "first", "second" },
            result.Select(e => e.Content).ToArray(),
            "Out-of-order appends must still read back chronologically so turn indexing is stable.");
    }

    [TestMethod]
    public async Task ReadSessionAsync_NonPositiveCap_ReturnsEmpty()
    {
        await AppendAsync("session/a", "user", "alpha", 0);

        Assert.AreEqual(0, (await _log.ReadSessionAsync("session/a", 0)).Count);
        Assert.AreEqual(0, (await _log.ReadSessionAsync("session/a", -1)).Count);
    }

    [TestMethod]
    public async Task ReadSessionAsync_SessionIdWithEscapedCharacters_StillMatches()
    {
        // The serializer escapes non-ASCII by default, so a raw substring pre-filter over the
        // JSON line would silently drop this session's own entries.
        const string SessionId = "session/café-ünïcode";
        await AppendAsync(SessionId, "user", "accented session", 0);
        await AppendAsync("session/other", "user", "unrelated", 1);

        var result = await _log.ReadSessionAsync(SessionId, 10);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("accented session", result[0].Content);
    }

    [TestMethod]
    public async Task ReadSessionAsync_SkipsMalformedLinesWithoutFailing()
    {
        await AppendAsync("session/a", "user", "good one", 0);
        await File.AppendAllTextAsync(Path.Combine(_root, "turns.jsonl"), "{not json}" + Environment.NewLine);
        await AppendAsync("session/a", "user", "good two", 1);

        var result = await _log.ReadSessionAsync("session/a", 10);

        Assert.AreEqual(2, result.Count);
    }

    // ── ListLoggedSessionsAsync ───────────────────────────────────────────

    [TestMethod]
    public async Task ListLoggedSessionsAsync_NoFile_ReturnsEmpty()
    {
        Assert.AreEqual(0, (await _log.ListLoggedSessionsAsync()).Count);
    }

    [TestMethod]
    public async Task ListLoggedSessionsAsync_ReportsCountsAndRanges()
    {
        await AppendAsync("session/a", "user", "one", 0);
        await AppendAsync("session/a", "assistant", "two", 4);
        await AppendAsync("patrol/heartbeat", "assistant", "ran", 2);

        var sessions = await _log.ListLoggedSessionsAsync();

        var a = sessions.Single(s => s.SessionId == "session/a");
        Assert.AreEqual(2, a.TurnCount);
        Assert.AreEqual(Origin, a.FirstTimestamp);
        Assert.AreEqual(Origin.AddMinutes(4), a.LastTimestamp);

        var patrol = sessions.Single(s => s.SessionId == "patrol/heartbeat");
        Assert.AreEqual(1, patrol.TurnCount);
    }

    [TestMethod]
    public async Task ListLoggedSessionsAsync_OrdersByMostRecentlyActive()
    {
        await AppendAsync("session/old", "user", "one", 0);
        await AppendAsync("session/new", "user", "two", 10);
        await AppendAsync("session/mid", "user", "three", 5);

        var sessions = await _log.ListLoggedSessionsAsync();

        CollectionAssert.AreEqual(
            new[] { "session/new", "session/mid", "session/old" },
            sessions.Select(s => s.SessionId).ToArray());
    }

    [TestMethod]
    public async Task ListLoggedSessionsAsync_AfterClear_ReturnsEmpty()
    {
        await AppendAsync("session/a", "user", "one", 0);
        await _log.ClearAsync();

        Assert.AreEqual(0, (await _log.ListLoggedSessionsAsync()).Count,
            "The dream cycle's clear must leave nothing for recall to find in the log.");
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileConversationMemoryTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rockbot-conv-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private FileConversationMemory CreateMemory(
        string? basePath = null, int maxTurnsPerSession = 50, int maxArchivedTurnsPerSession = 500)
    {
        var convOptions    = Options.Create(new ConversationMemoryOptions
        {
            BasePath = basePath ?? "conversations",
            MaxTurnsPerSession = maxTurnsPerSession,
            MaxArchivedTurnsPerSession = maxArchivedTurnsPerSession
        });
        var inner = new InMemoryConversationMemory(
            convOptions,
            NullLogger<InMemoryConversationMemory>.Instance);

        var profileOptions = Options.Create(new AgentProfileOptions { BasePath = _tempDir });

        return new FileConversationMemory(
            inner, convOptions, profileOptions,
            NullLogger<FileConversationMemory>.Instance);
    }

    // ── ResolvePath ───────────────────────────────────────────────────────────

    [TestMethod]
    public void ResolvePath_RelativePath_CombinesUnderProfile()
    {
        var result = FileConversationMemory.ResolvePath("conversations", "/data/agent");
        Assert.AreEqual(Path.Combine("/data/agent", "conversations"), result);
    }

    [TestMethod]
    public void ResolvePath_AbsolutePath_UsedAsIs()
    {
        var result = FileConversationMemory.ResolvePath("/custom/path", "/data/agent");
        Assert.AreEqual("/custom/path", result);
    }

    // ── Slash-namespaced session IDs ──────────────────────────────────────────

    [TestMethod]
    public async Task AddTurnAsync_SessionIdWithSlash_CreatesSubdirectoryAndFile()
    {
        using var memory = CreateMemory();

        // "session/blazor-session" was the session ID that caused the bug —
        // FileConversationMemory must create the 'session/' subdirectory automatically.
        const string sessionId = "session/blazor-session";
        var turn = new ConversationTurn("user", "hello", DateTimeOffset.UtcNow);

        await memory.AddTurnAsync(sessionId, turn);

        var expectedFile = Path.Combine(_tempDir, "conversations", "session", "blazor-session.json");
        Assert.IsTrue(File.Exists(expectedFile),
            $"Expected file at {expectedFile} to be created for session ID '{sessionId}'");
    }

    [TestMethod]
    public async Task AddTurnAsync_SessionIdWithSlash_RoundTripsOnStartup()
    {
        const string sessionId = "session/blazor-session";
        var turn = new ConversationTurn("user", "test content", DateTimeOffset.UtcNow);

        // Write via one instance
        using (var memory = CreateMemory())
            await memory.AddTurnAsync(sessionId, turn);

        // Restore via a new instance's StartAsync
        using var restored = CreateMemory();
        await restored.StartAsync(CancellationToken.None);

        var turns = await restored.GetTurnsAsync(sessionId);
        Assert.AreEqual(1, turns.Count, "Turn should survive a restart when session ID contains '/'");
        Assert.AreEqual(turn.Content, turns[0].Content);
    }

    [TestMethod]
    public async Task ListSessionsAsync_SessionIdWithSlash_IsIncluded()
    {
        const string sessionId = "session/blazor-session";
        var turn = new ConversationTurn("assistant", "hi", DateTimeOffset.UtcNow);

        using var memory = CreateMemory();
        await memory.AddTurnAsync(sessionId, turn);

        var sessions = await memory.ListSessionsAsync();
        CollectionAssert.Contains(sessions.ToList(), sessionId,
            "ListSessionsAsync must return slash-containing session IDs from subdirectories");
    }

    [TestMethod]
    public async Task AddTurnAsync_FlatAndNestedSessionIds_BothPersist()
    {
        using var memory = CreateMemory();
        var turn = new ConversationTurn("user", "msg", DateTimeOffset.UtcNow);

        await memory.AddTurnAsync("flat-session", turn);
        await memory.AddTurnAsync("session/nested-session", turn);

        var sessions = await memory.ListSessionsAsync();
        var list = sessions.ToList();
        CollectionAssert.Contains(list, "flat-session");
        CollectionAssert.Contains(list, "session/nested-session");
    }

    // ── #509: archive of turns evicted past MaxTurnsPerSession ───────────────

    private static async Task AddTurnsAsync(FileConversationMemory memory, string sessionId, int from, int count)
    {
        for (var i = from; i < from + count; i++)
            await memory.AddTurnAsync(sessionId, new ConversationTurn(i % 2 == 0 ? "user" : "assistant", $"t{i}", DateTimeOffset.UtcNow));
    }

    private static string[] Contents(IReadOnlyList<ConversationTurn> turns) => [.. turns.Select(t => t.Content)];

    [TestMethod]
    public async Task EvictedTurns_AreArchivedOldestFirst_AndRetentionIsUnchanged()
    {
        using var memory = CreateMemory(maxTurnsPerSession: 3);

        await AddTurnsAsync(memory, "session/s", 0, 5);

        CollectionAssert.AreEqual(new[] { "t2", "t3", "t4" }, Contents(await memory.GetTurnsAsync("session/s")));
        CollectionAssert.AreEqual(new[] { "t0", "t1" }, Contents(await memory.GetArchivedTurnsAsync("session/s")));
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "conversations-archive", "session", "s.jsonl")));
    }

    [TestMethod]
    public async Task Archive_KeepsOnlyTheMostRecentEvictedTurns()
    {
        using var memory = CreateMemory(maxTurnsPerSession: 2, maxArchivedTurnsPerSession: 4);

        // 2 retained + 20 evicted: the file is cut back each time it runs a quarter over the cap.
        await AddTurnsAsync(memory, "s", 0, 22);

        CollectionAssert.AreEqual(new[] { "t16", "t17", "t18", "t19" }, Contents(await memory.GetArchivedTurnsAsync("s")));
        var lines = File.ReadAllLines(Path.Combine(_tempDir, "conversations-archive", "s.jsonl")).Length;
        Assert.IsTrue(lines <= 5, $"archive file should stay near its cap, has {lines} lines");
    }

    [TestMethod]
    public async Task Archive_SurvivesARestart()
    {
        using (var memory = CreateMemory(maxTurnsPerSession: 2))
            await AddTurnsAsync(memory, "s", 0, 4);

        using var restored = CreateMemory(maxTurnsPerSession: 2);
        await restored.StartAsync(CancellationToken.None);
        await AddTurnsAsync(restored, "s", 4, 1);

        CollectionAssert.AreEqual(new[] { "t0", "t1", "t2" }, Contents(await restored.GetArchivedTurnsAsync("s")));
    }

    [TestMethod]
    public async Task Restore_WithALoweredLimit_ArchivesTheOverflowOnce()
    {
        using (var memory = CreateMemory(maxTurnsPerSession: 5))
            await AddTurnsAsync(memory, "s", 0, 5);

        using (var lowered = CreateMemory(maxTurnsPerSession: 3))
        {
            await lowered.StartAsync(CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "t0", "t1" }, Contents(await lowered.GetArchivedTurnsAsync("s")));
        }

        // The session file was rewritten, so a second restart does not archive t0 and t1 again.
        using var again = CreateMemory(maxTurnsPerSession: 3);
        await again.StartAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "t0", "t1" }, Contents(await again.GetArchivedTurnsAsync("s")));
    }

    [TestMethod]
    public async Task ClearAsync_DiscardsTheArchive()
    {
        using var memory = CreateMemory(maxTurnsPerSession: 2);
        await AddTurnsAsync(memory, "s", 0, 4);

        await memory.ClearAsync("s");
        await AddTurnsAsync(memory, "s", 10, 1);

        Assert.AreEqual(0, (await memory.GetArchivedTurnsAsync("s")).Count);
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "conversations-archive", "s.jsonl")));
    }

    [TestMethod]
    public async Task ExpiredSession_ArchiveIsNotRecalled_AndIsDiscardedByTheNextTurn()
    {
        using (var memory = CreateMemory(maxTurnsPerSession: 2))
            await AddTurnsAsync(memory, "s", 0, 4);

        // A restart that does not restore the session stands in for idle expiry: the files remain,
        // but the conversation they belong to has ended.
        using var fresh = CreateMemory(maxTurnsPerSession: 2);
        Assert.AreEqual(0, (await fresh.GetArchivedTurnsAsync("s")).Count, "no live session, nothing to recall");

        await AddTurnsAsync(fresh, "s", 10, 3);

        CollectionAssert.AreEqual(new[] { "t10" }, Contents(await fresh.GetArchivedTurnsAsync("s")),
            "the new conversation's archive holds only its own evictions");
    }

    [TestMethod]
    public async Task ZeroArchiveLimit_DiscardsEvictedTurns()
    {
        using var memory = CreateMemory(maxTurnsPerSession: 2, maxArchivedTurnsPerSession: 0);

        await AddTurnsAsync(memory, "s", 0, 4);

        Assert.AreEqual(0, (await memory.GetArchivedTurnsAsync("s")).Count);
        Assert.IsFalse(Directory.Exists(Path.Combine(_tempDir, "conversations-archive"))
            && Directory.EnumerateFiles(Path.Combine(_tempDir, "conversations-archive")).Any());
    }
}

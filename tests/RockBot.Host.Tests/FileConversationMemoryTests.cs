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

    private FileConversationMemory CreateMemory(string? basePath = null)
    {
        var inner = new InMemoryConversationMemory(
            Options.Create(new ConversationMemoryOptions()),
            NullLogger<InMemoryConversationMemory>.Instance);

        var profileOptions = Options.Create(new AgentProfileOptions { BasePath = _tempDir });
        var convOptions    = Options.Create(new ConversationMemoryOptions
        {
            BasePath = basePath ?? "conversations"
        });

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
}

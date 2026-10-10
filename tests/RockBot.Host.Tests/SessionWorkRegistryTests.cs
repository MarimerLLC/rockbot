using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// The per-conversation work registry (#665): subagent results and the files and uploads the
/// conversation wrote, whichever rung wrote them, bounded and expiring.
/// </summary>
[TestClass]
public class SessionWorkRegistryTests
{
    private static IEnumerable<KeyValuePair<string, object?>> Args(params (string Key, object? Value)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value));

    private static SubagentWorkResult Result(string taskId, string description, string output = "done",
        DateTimeOffset? at = null, params string[] keys) =>
        SubagentWorkResult.Create(taskId, description, output, keys, isSuccess: true, at ?? DateTimeOffset.UtcNow);

    // ── Results ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void SubagentResult_RecordedUnderNamespace_IsVisibleUnderRawSessionId()
    {
        var registry = new SessionWorkRegistry();

        registry.RecordSubagentResult("session/cli-deck", Result("6a393b675e2f", "Research the MCP v2 spec"));

        var snapshot = registry.GetSnapshot("cli-deck");
        Assert.AreEqual(1, snapshot.Results.Count);
        Assert.AreEqual("6a393b675e2f", snapshot.Results[0].TaskId);
        Assert.AreEqual("cli-deck", snapshot.SessionId);
    }

    [TestMethod]
    public void SubagentResult_SplitsBulkChunkKeysIntoACount()
    {
        var result = SubagentWorkResult.Create("t1", "research", "output", [
            "subagent/t1/mcp-summary",
            "subagent/t1/web-https___modelcontextprotocol.io_spec-chunk0",
            "subagent/t1/web-https___modelcontextprotocol.io_spec-chunk1",
            "subagent/t1/web-https___modelcontextprotocol.io_spec-index",
        ], isSuccess: true, DateTimeOffset.UtcNow);

        CollectionAssert.AreEqual(new[] { "subagent/t1/mcp-summary" }, result.Keys.ToArray());
        Assert.AreEqual(3, result.ChunkKeyCount);
    }

    [TestMethod]
    public void SubagentResult_SummaryIsTheOpeningOfTheOutput_AndOutputIsCapped()
    {
        var output = new string('x', 40_000);
        var result = SubagentWorkResult.Create("t1", new string('d', 1_000), output, [], true, DateTimeOffset.UtcNow);

        Assert.IsTrue(result.Summary.Length <= SessionWorkRegistry.MaxSummaryChars + 1);
        Assert.IsTrue(result.Output.Length <= SessionWorkRegistry.MaxOutputChars + 1);
        Assert.IsTrue(result.Description.Length <= SessionWorkRegistry.MaxDescriptionChars + 1);
    }

    [TestMethod]
    public void Results_AreBounded_KeepingTheNewest()
    {
        var registry = new SessionWorkRegistry(new SessionWorkRegistryOptions { MaxResultsPerSession = 20 });
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        for (var i = 0; i < 25; i++)
            registry.RecordSubagentResult("s1", Result($"t{i}", $"task {i}", at: start.AddMinutes(i)));

        var snapshot = registry.GetSnapshot("s1");
        Assert.AreEqual(20, snapshot.Results.Count);
        Assert.AreEqual("t24", snapshot.Results[0].TaskId, "Newest first.");
        Assert.IsFalse(snapshot.Results.Any(r => r.TaskId == "t0"), "Oldest dropped.");
    }

    [TestMethod]
    public void Entries_ExpireAfterTheConfiguredWindow()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var registry = new SessionWorkRegistry(new SessionWorkRegistryOptions { Expiry = TimeSpan.FromHours(24) }, time);

        registry.RecordSubagentResult("s1", Result("t1", "research", at: time.GetUtcNow()));
        registry.RecordToolCall("s1", "file_write", Args(("path", "drafts/a.md"), ("content", "x")), succeeded: true);
        Assert.IsFalse(registry.GetSnapshot("s1").IsEmpty);

        time.Advance(TimeSpan.FromHours(25));

        Assert.IsTrue(registry.GetSnapshot("s1").IsEmpty);
    }

    // ── Artifacts ────────────────────────────────────────────────────────────

    [TestMethod]
    public void FileWrite_ByPrimary_IsRecorded()
    {
        var registry = new SessionWorkRegistry();

        registry.RecordToolCall("s1", "file_write", Args(("path", "drafts/deck.md"), ("content", "# Deck")), succeeded: true);

        var artifact = registry.GetSnapshot("session/s1").Artifacts.Single();
        Assert.AreEqual("drafts/deck.md", artifact.Path);
        Assert.AreEqual("s1", artifact.LastWriterSessionId);
        Assert.AreEqual("file_write", artifact.LastTool);
    }

    [TestMethod]
    public void FileEdit_BySubagent_CountsTowardThePrimary()
    {
        var registry = new SessionWorkRegistry();
        registry.LinkSession("subagent-abc123", "session/s1");

        registry.RecordToolCall("subagent-abc123", "file_edit",
            Args(("path", "drafts/deck.md"), ("old_string", "a"), ("new_string", "b")), succeeded: true);
        // The subagent's registry tools use its namespace form; it resolves to the same subagent.
        registry.RecordToolCall("subagent/abc123", "file_write",
            Args(("path", "drafts/outline.md"), ("content", "x")), succeeded: true);

        var snapshot = registry.GetSnapshot("s1");
        Assert.AreEqual(2, snapshot.Artifacts.Count);
        Assert.IsTrue(snapshot.Artifacts.All(a => a.LastWriterSessionId == "subagent-abc123"));
    }

    [TestMethod]
    public void WispUnderSubagent_ResolvesToThePrimary()
    {
        var registry = new SessionWorkRegistry();
        registry.LinkSession("subagent-abc123", "session/s1");
        registry.LinkSession("wisp-0123456789a", "subagent/abc123");

        registry.RecordToolCall("wisp-0123456789a", "file_write", Args(("path", "out.md"), ("content", "x")), true);

        Assert.AreEqual("s1", registry.ResolveRootSession("wisp-0123456789a"));
        Assert.AreEqual("out.md", registry.GetSnapshot("s1").Artifacts.Single().Path);
    }

    [TestMethod]
    public void IsSessionWithin_FollowsLinks_ButNotAcrossSiblings()
    {
        var registry = new SessionWorkRegistry();
        registry.LinkSession("subagent-abc123", "session/s1");
        registry.LinkSession("subagent-def456", "session/s1");
        registry.LinkSession("wisp-0123456789a", "subagent/abc123");

        Assert.IsTrue(registry.IsSessionWithin("wisp-0123456789a", "subagent-abc123"), "a subagent's wisp is within it");
        Assert.IsTrue(registry.IsSessionWithin("subagent/abc123", "subagent-abc123"), "spellings normalize");
        Assert.IsTrue(registry.IsSessionWithin("wisp-0123456789a", "session/s1"));
        Assert.IsFalse(registry.IsSessionWithin("wisp-0123456789a", "subagent-def456"), "not a sibling's");
        Assert.IsFalse(registry.IsSessionWithin("s1", "subagent-abc123"), "a parent is not within its child");
        Assert.IsFalse(registry.IsSessionWithin("", "subagent-abc123"));
    }

    [TestMethod]
    public void McpInvokeUpload_RecordsTheRemoteTargetOnTheLocalFile()
    {
        var registry = new SessionWorkRegistry();
        registry.RecordToolCall("s1", "file_write", Args(("path", "drafts/deck.md"), ("content", "x")), true);

        var inner = JsonSerializer.SerializeToElement(new { local_path = "drafts/deck.md", remote_path = "/Talks/deck.md" });
        registry.RecordToolCall("s1", "mcp_invoke_tool",
            Args(("server_name", "onedrive"), ("tool_name", "upload_file"), ("arguments", inner)), true);

        var artifact = registry.GetSnapshot("s1").Artifacts.Single();
        Assert.AreEqual("drafts/deck.md", artifact.Path);
        Assert.AreEqual("onedrive:/Talks/deck.md", artifact.RemoteTarget);
        Assert.IsFalse(artifact.IsRemoteOnly);
    }

    [TestMethod]
    public void McpInvokeUpload_WithStringifiedArguments_IsParsed()
    {
        var registry = new SessionWorkRegistry();
        registry.RecordToolCall("s1", "mcp_invoke_tool",
            Args(("server_name", "onedrive"), ("tool_name", "upload_file"),
                 ("arguments", "{\"remote_path\":\"/Talks/notes.md\"}")), true);

        var artifact = registry.GetSnapshot("s1").Artifacts.Single();
        Assert.AreEqual("onedrive:/Talks/notes.md", artifact.Path);
        Assert.IsTrue(artifact.IsRemoteOnly);
    }

    [TestMethod]
    public void TypedUploadTool_IsRecorded()
    {
        var registry = new SessionWorkRegistry();
        registry.RecordToolCall("s1", "sharepoint__upload_document",
            Args(("local_path", "drafts/a.pptx"), ("remote_path", "Shared/a.pptx")), true);

        var artifact = registry.GetSnapshot("s1").Artifacts.Single();
        Assert.AreEqual("drafts/a.pptx", artifact.Path);
        Assert.AreEqual("sharepoint:Shared/a.pptx", artifact.RemoteTarget);
    }

    [TestMethod]
    public void ReadsFailuresAndRemoteFileTools_AreIgnored()
    {
        var registry = new SessionWorkRegistry();
        registry.RecordToolCall("s1", "file_read", Args(("path", "drafts/a.md")), true);
        registry.RecordToolCall("s1", "file_list", Args(("prefix", "drafts/")), true);
        registry.RecordToolCall("s1", "file_write", Args(("path", "drafts/b.md"), ("content", "x")), succeeded: false);
        registry.RecordToolCall("s1", "onedrive__file_write", Args(("path", "c.md")), true);
        registry.RecordToolCall("s1", "save_to_working_memory", Args(("key", "k"), ("data", "d")), true);

        Assert.IsTrue(registry.GetSnapshot("s1").IsEmpty);
    }

    [TestMethod]
    public void FileDelete_RemovesTheArtifact()
    {
        var registry = new SessionWorkRegistry();
        registry.RecordToolCall("s1", "file_write", Args(("path", "drafts/a.md"), ("content", "x")), true);
        registry.RecordToolCall("s1", "file_delete", Args(("path", "drafts/a.md")), true);

        Assert.IsTrue(registry.GetSnapshot("s1").IsEmpty);
    }

    [TestMethod]
    public void FileMove_RenamesTheArtifact()
    {
        var registry = new SessionWorkRegistry();
        registry.RecordToolCall("s1", "file_write", Args(("path", "drafts/a.md"), ("content", "x")), true);
        registry.RecordToolCall("s1", "file_move", Args(("path", "drafts/a.md"), ("destination", "final/a.md")), true);

        Assert.AreEqual("final/a.md", registry.GetSnapshot("s1").Artifacts.Single().Path);
    }

    [TestMethod]
    public void Artifacts_AreBounded_KeepingTheMostRecent()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var registry = new SessionWorkRegistry(new SessionWorkRegistryOptions { MaxArtifactsPerSession = 50 }, time);
        for (var i = 0; i < 60; i++)
        {
            registry.RecordToolCall("s1", "file_write", Args(("path", $"f{i}.md"), ("content", "x")), true);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var artifacts = registry.GetSnapshot("s1").Artifacts;
        Assert.AreEqual(50, artifacts.Count);
        Assert.AreEqual("f59.md", artifacts[0].Path);
        Assert.IsFalse(artifacts.Any(a => a.Path == "f0.md"));
    }

    [TestMethod]
    public void SessionsAreIsolated()
    {
        var registry = new SessionWorkRegistry();
        registry.RecordToolCall("s1", "file_write", Args(("path", "a.md"), ("content", "x")), true);

        Assert.IsTrue(registry.GetSnapshot("s2").IsEmpty);
    }

    // ── The ledger hook ──────────────────────────────────────────────────────

    [TestMethod]
    public void Ledger_HandsRawArgumentsToItsObserver()
    {
        var registry = new SessionWorkRegistry();
        var ledger = new LoopToolCallLedger((name, raw, ok) => registry.RecordToolCall("s1", name, raw, ok));

        ledger.Record("file_write", "path=drafts/a.md, content=…", true,
            rawArguments: Args(("path", "drafts/a.md"), ("content", "full body")));

        Assert.AreEqual("drafts/a.md", registry.GetSnapshot("s1").Artifacts.Single().Path);
        Assert.AreEqual(1, ledger.Snapshot().Count);
    }

    [TestMethod]
    public void Ledger_IgnoresAThrowingObserver()
    {
        var ledger = new LoopToolCallLedger((_, _, _) => throw new InvalidOperationException("boom"));

        ledger.Record("file_write", null, true);

        Assert.AreEqual(1, ledger.Snapshot().Count);
    }

    // ── Primary context section ──────────────────────────────────────────────

    [TestMethod]
    public void RenderForPrimary_NamesPathsUploadsAndResultKeys()
    {
        var registry = new SessionWorkRegistry();
        registry.LinkSession("subagent-6a393b675e2f", "session/s1");
        registry.RecordToolCall("subagent-6a393b675e2f", "file_write",
            Args(("path", "drafts/mcp-v2-deck.md"), ("content", "x")), true);
        registry.RecordToolCall("s1", "onedrive__upload_file",
            Args(("local_path", "drafts/mcp-v2-deck.md"), ("remote_path", "/Talks/mcp-v2-deck.md")), true);
        registry.RecordSubagentResult("s1", Result("6a393b675e2f", "Research the key features of the MCP v2 spec",
            keys: "subagent/6a393b675e2f/mcp-2026-07-28-primary-source-summary"));

        var text = SessionWorkContext.RenderForPrimary(registry.GetSnapshot("s1"))!;

        StringAssert.StartsWith(text, "Work products in this conversation");
        StringAssert.Contains(text, "drafts/mcp-v2-deck.md");
        StringAssert.Contains(text, "onedrive:/Talks/mcp-v2-deck.md");
        StringAssert.Contains(text, "subagent/6a393b675e2f/mcp-2026-07-28-primary-source-summary");
        StringAssert.Contains(text, "Research the key features of the MCP v2 spec");
    }

    [TestMethod]
    public void RenderForPrimary_IsCapped()
    {
        var registry = new SessionWorkRegistry();
        for (var i = 0; i < 50; i++)
            registry.RecordToolCall("s1", "file_write", Args(("path", $"drafts/a-rather-long-file-name-number-{i}.md"), ("content", "x")), true);
        for (var i = 0; i < 20; i++)
            registry.RecordSubagentResult("s1", Result($"task{i:D8}", new string('d', 200), keys: $"subagent/task{i:D8}/result"));

        var text = SessionWorkContext.RenderForPrimary(registry.GetSnapshot("s1"))!;

        Assert.IsTrue(text.Length <= SessionWorkContext.DefaultMaxChars, $"Length {text.Length}");
        StringAssert.Contains(text, "more not shown");
    }

    [TestMethod]
    public void RenderForPrimary_EmptyConversation_IsNull()
    {
        Assert.IsNull(SessionWorkContext.RenderForPrimary(SessionWorkSnapshot.Empty("s1")));
    }

    [TestMethod]
    public async Task ContextBuilder_UserSession_IncludesTheWorkProductsSection()
    {
        var registry = new SessionWorkRegistry();
        registry.RecordToolCall("s1", "file_write", Args(("path", "drafts/deck.md"), ("content", "x")), true);
        var builder = BuildBuilder(registry);

        var messages = await builder.BuildAsync("s1", "update the deck", CancellationToken.None);

        var section = messages.Single(m => m.Text.StartsWith("Work products in this conversation", StringComparison.Ordinal));
        StringAssert.Contains(section.Text, "drafts/deck.md");
        Assert.IsTrue(section.Text.Length <= SessionWorkContext.DefaultMaxChars);
    }

    [TestMethod]
    public async Task ContextBuilder_SubagentNamespace_LeavesTheSectionOut()
    {
        var registry = new SessionWorkRegistry();
        registry.RecordToolCall("s1", "file_write", Args(("path", "drafts/deck.md"), ("content", "x")), true);
        var builder = BuildBuilder(registry);

        var messages = await builder.BuildAsync("subagent-x", "update the deck", CancellationToken.None,
            workingMemoryNamespace: "subagent/x");

        Assert.IsFalse(messages.Any(m => m.Text.StartsWith("Work products in this conversation", StringComparison.Ordinal)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static AgentContextBuilder BuildBuilder(ISessionWorkRegistry registry)
    {
        var profileHolder = new ProfileHolder();
        var doc = new AgentProfileDocument("soul", null, [], "I am a test agent.");
        profileHolder.Update(new AgentProfile(doc, doc));
        var nameHolder = new AgentNameHolder();

        var agentProfileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-workreg-test-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(agentProfileOptions.Value.BasePath);

        var clock = new AgentClock(
            new ConfigurationBuilder().Build(),
            agentProfileOptions,
            NullLoggerFactory.Instance.CreateLogger<AgentClock>());

        return new AgentContextBuilder(
            profileHolder: profileHolder,
            agent: new AgentIdentity("TestBot"),
            promptBuilder: new DefaultSystemPromptBuilder(profileHolder, nameHolder, Options.Create(new AgentProfileOptions())),
            rulesStore: new StubRulesStore(),
            modelBehavior: ModelBehavior.Default,
            conversationMemory: new StubConversationMemory(),
            longTermMemory: new StubLongTermMemory(),
            injectedMemoryTracker: new InjectedMemoryTracker(),
            workingMemory: new StubWorkingMemory(),
            skillStore: new StubSkillStore(),
            skillIndexTracker: new SkillIndexTracker(),
            skillRecallTracker: new SkillRecallTracker(),
            clock: clock,
            serviceSearchIndexProviders: [],
            knowledgeGraphProviders: [],
            knowledgeGraphOptions: Options.Create(new KnowledgeGraphOptions()),
            embeddingGenerators: [],
            logger: NullLogger<AgentContextBuilder>.Instance,
            sessionWorkRegistry: registry);
    }

    private sealed class StubConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubLongTermMemory : ILongTermMemory
    {
        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(null);
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubWorkingMemory : IWorkingMemory
    {
        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null) => Task.CompletedTask;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }

    private sealed class StubSkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }

    private sealed class StubRulesStore : IRulesStore
    {
        public IReadOnlyList<string> Rules => [];
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddAsync(string rule) => Task.CompletedTask;
        public Task RemoveAsync(string rule) => Task.CompletedTask;
    }
}

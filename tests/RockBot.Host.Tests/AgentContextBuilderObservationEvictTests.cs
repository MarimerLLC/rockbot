using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// Amendment 1 step 4 tests for the working-memory observation-eviction filter
/// in <see cref="AgentContextBuilder"/>. Stubs the tool-call log so each
/// branch (newer success, older success, no match, no observation tag,
/// unwired log) can be exercised deterministically.
/// </summary>
[TestClass]
public class AgentContextBuilderObservationEvictTests
{
    private const string LongMessage = "tell me what you remember about this topic";

    [TestMethod]
    public async Task BuildAsync_StaleObservation_ContradictedByNewerSuccess_IsEvicted()
    {
        var observedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var stale = ObservationEntry(
            "patrol/email-triage-latest",
            "calendar-mcp/search_emails wrapper cannot pass arguments",
            observedAt);

        var wm = new RecordingWorkingMemory(patrol: [stale]);
        var log = new StubToolCallLog(
        [
            // Successful inner call to search_emails on calendar-mcp, AFTER observedAt.
            new ToolCallEvent(
                SessionId: "any-session",
                ToolName: "mcp_invoke_tool",
                ArgumentsSummary: "server_name=calendar-mcp, tool_name=search_emails, arguments={}",
                Succeeded: true,
                DurationMs: 12,
                Timestamp: observedAt.AddMinutes(30))
        ]);
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        CollectionAssert.Contains(wm.DeletedKeys, "patrol/email-triage-latest");
    }

    [TestMethod]
    public async Task BuildAsync_Observation_OnlyOlderSuccesses_NotEvicted()
    {
        // Mitigation against false eviction: a success that PREDATES the
        // observation must not falsify it. The observation could be reporting
        // a genuine new failure.
        var observedAt = DateTimeOffset.UtcNow;
        var fresh = ObservationEntry(
            "patrol/email-triage-latest",
            "calendar-mcp/search_emails wrapper cannot pass arguments",
            observedAt);

        var wm = new RecordingWorkingMemory(patrol: [fresh]);
        var log = new StubToolCallLog(
        [
            new ToolCallEvent(
                SessionId: "any-session",
                ToolName: "mcp_invoke_tool",
                ArgumentsSummary: "server_name=calendar-mcp, tool_name=search_emails",
                Succeeded: true,
                DurationMs: 12,
                Timestamp: observedAt.AddMinutes(-30))
        ]);
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        Assert.AreEqual(0, wm.DeletedKeys.Count,
            "Observations are only evicted when contradicted by a NEWER success.");
    }

    [TestMethod]
    public async Task BuildAsync_Observation_OnlyFailedCalls_NotEvicted()
    {
        var observedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var stale = ObservationEntry(
            "patrol/email-triage-latest",
            "calendar-mcp/search_emails wrapper cannot pass arguments",
            observedAt);

        var wm = new RecordingWorkingMemory(patrol: [stale]);
        var log = new StubToolCallLog(
        [
            new ToolCallEvent(
                SessionId: "any-session",
                ToolName: "mcp_invoke_tool",
                ArgumentsSummary: "server_name=calendar-mcp, tool_name=search_emails",
                Succeeded: false,
                DurationMs: 12,
                Timestamp: observedAt.AddMinutes(30))
        ]);
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        Assert.AreEqual(0, wm.DeletedKeys.Count,
            "Failed calls don't contradict an observation — the observation may be correct.");
    }

    [TestMethod]
    public async Task BuildAsync_NoToolReferenceInObservation_NotEvicted()
    {
        var stale = ObservationEntry(
            "patrol/something",
            "the wrapper is blocked because of an unrelated reason",
            DateTimeOffset.UtcNow.AddHours(-1));

        var wm = new RecordingWorkingMemory(patrol: [stale]);
        var log = new StubToolCallLog([]);
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        Assert.AreEqual(0, wm.DeletedKeys.Count,
            "Without a server/tool reference, the filter has nothing to verify against.");
    }

    [TestMethod]
    public async Task BuildAsync_NoObservationTag_NotChecked()
    {
        // Even with capability-claim language and a tool reference, an entry
        // without the kind=observation tag is NOT touched. The soft-gate tag is
        // what marks an entry as falsifiable via this heuristic.
        var entry = new WorkingMemoryEntry(
            Key: "patrol/x",
            Value: "calendar-mcp/search_emails is blocked",
            StoredAt: DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(23),
            Category: "patrol/email",
            Tags: ["heartbeat", "email"]);

        var wm = new RecordingWorkingMemory(patrol: [entry]);
        var log = new StubToolCallLog(
        [
            new ToolCallEvent("s", "mcp_invoke_tool",
                "server_name=calendar-mcp, tool_name=search_emails",
                Succeeded: true, DurationMs: 1,
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(-30))
        ]);
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        Assert.AreEqual(0, wm.DeletedKeys.Count);
    }

    [TestMethod]
    public async Task BuildAsync_NoToolCallLogConfigured_FilterIsNoop()
    {
        var stale = ObservationEntry(
            "patrol/email-triage-latest",
            "calendar-mcp/search_emails wrapper cannot pass arguments",
            DateTimeOffset.UtcNow.AddHours(-1));

        var wm = new RecordingWorkingMemory(patrol: [stale]);
        var builder = NewBuilder(wm, toolCallLog: null);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        Assert.AreEqual(0, wm.DeletedKeys.Count,
            "Without a tool-call log the filter must be a no-op (back-compat).");
    }

    [TestMethod]
    public async Task BuildAsync_ToolCallLogThrows_FilterIsNoop()
    {
        var stale = ObservationEntry(
            "patrol/email-triage-latest",
            "calendar-mcp/search_emails wrapper cannot pass arguments",
            DateTimeOffset.UtcNow.AddHours(-1));

        var wm = new RecordingWorkingMemory(patrol: [stale]);
        var log = new ThrowingToolCallLog();
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        Assert.AreEqual(0, wm.DeletedKeys.Count,
            "Log query failures must not break context building; entries pass through.");
    }

    [TestMethod]
    public async Task BuildAsync_WritingCall_NotTreatedAsContradiction()
    {
        // Reproduces the self-eviction loop seen in the heartbeat patrol:
        // worker saves to shared/patrol/active-plans-latest, content contains
        // "blocked" (a legitimate status value), and the regex extracts
        // "shared/patrol" as a pseudo-(server, tool). Before the fix, the
        // worker's OWN SaveToWorkingMemory call was treated as the contradicting
        // successful tool call, instantly evicting the just-saved entry.
        // After the fix, calls whose args reference the entry's own key are
        // excluded from the contradiction set.
        var observedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var stale = ObservationEntry(
            "shared/patrol/active-plans-latest",
            "Active plans status: pending=2, in-progress=1, blocked=0. " +
            "Cross-references: shared/patrol/heartbeat-active-plans-tasks-latest.",
            observedAt);

        var wm = new RecordingWorkingMemory(shared: [stale]);
        var log = new StubToolCallLog(
        [
            // The writing call itself — args contain the entry's key.
            new ToolCallEvent("s", "save_to_working_memory",
                "key=shared/patrol/active-plans-latest, category=patrol/heartbeat, ttl_minutes=300",
                Succeeded: true, DurationMs: 19,
                Timestamp: observedAt.AddMilliseconds(50)),
        ]);
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        Assert.AreEqual(0, wm.DeletedKeys.Count,
            "A SaveToWorkingMemory call to the entry's own key cannot count as " +
            "contradicting evidence — it's the writing call, not external proof.");
    }

    [TestMethod]
    public async Task BuildAsync_NamespacePrefixesInContent_NotExtractedAsRefs()
    {
        // Working-memory key paths like shared/patrol/... and long-term-memory
        // category paths like user-preferences/family/... follow the same
        // <segment>/<segment> shape that the tool-reference regex matches.
        // After the fix, namespace prefixes are filtered out so they don't
        // produce false (server, tool) pairs. Without a real reference, the
        // observation has nothing to verify against and stays put.
        var observedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var stale = ObservationEntry(
            "patrol/email-triage-latest",
            "Patrol findings blocked rendering. References: " +
            "shared/patrol/heartbeat, user-preferences/family, agent-identity/self-model.",
            observedAt);

        var wm = new RecordingWorkingMemory(patrol: [stale]);
        // Even a successful call that looks like it could match a path segment
        // (e.g. "patrol" or "shared") must not trigger eviction.
        var log = new StubToolCallLog(
        [
            new ToolCallEvent("s", "save_to_working_memory",
                "key=shared/patrol/something-else",
                Succeeded: true, DurationMs: 1,
                Timestamp: observedAt.AddMinutes(30)),
        ]);
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        Assert.AreEqual(0, wm.DeletedKeys.Count,
            "Namespace prefixes are not server names — without a real reference, " +
            "no eviction should occur.");
    }

    [TestMethod]
    public async Task BuildAsync_RealClaimAndDifferentKeyWritingCall_StillEvicted()
    {
        // Regression test: the writing-call filter must not be so broad that
        // it stops legitimate eviction. A real claim about calendar-mcp/search_emails
        // with a successful mcp_invoke_tool call to that server/tool — and a
        // SaveToWorkingMemory writing call to a DIFFERENT key — must still evict.
        var observedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var stale = ObservationEntry(
            "patrol/email-triage-latest",
            "calendar-mcp/search_emails wrapper cannot pass arguments",
            observedAt);

        var wm = new RecordingWorkingMemory(patrol: [stale]);
        var log = new StubToolCallLog(
        [
            // The writing call to a different key — must not be treated as evidence.
            new ToolCallEvent("s", "save_to_working_memory",
                "key=patrol/something-else",
                Succeeded: true, DurationMs: 1,
                Timestamp: observedAt.AddMinutes(10)),
            // The real contradicting MCP call.
            new ToolCallEvent("s", "mcp_invoke_tool",
                "server_name=calendar-mcp, tool_name=search_emails",
                Succeeded: true, DurationMs: 1,
                Timestamp: observedAt.AddMinutes(30)),
        ]);
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        CollectionAssert.Contains(wm.DeletedKeys, "patrol/email-triage-latest",
            "Real capability claims must still be evicted when a genuine " +
            "successful MCP call contradicts them.");
    }

    [TestMethod]
    public async Task BuildAsync_SaveToWorkingMemoryWithMcpRefsInData_NotContradiction()
    {
        // Real-world case from the heartbeat patrol: the email worker saved
        // findings containing a genuine MCP reference like calendar-mcp/get_emails.
        // The reference extracts correctly. The calendar worker then saved its
        // own findings to a different key, but its JSON data blob mentions
        // "calendar-mcp" and "get_emails" as substrings. With the old loose
        // substring match, that triggered a false eviction. After the fix,
        // CallMatchesAnyRef only accepts mcp_invoke_tool calls with the
        // structured server_name=X, tool_name=Y form, so the sibling
        // SaveToWorkingMemory call is rejected.
        var observedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var stale = ObservationEntry(
            "shared/patrol/email-latest",
            "Patrol blocked by no actionable items. Queried calendar-mcp/get_emails across all accounts.",
            observedAt);

        var wm = new RecordingWorkingMemory(shared: [stale]);
        var log = new StubToolCallLog(
        [
            // A sibling worker's SaveToWorkingMemory call whose data blob
            // happens to contain "calendar-mcp" and "get_emails" as substrings
            // (a real MCP server name and a tool name mentioned in JSON data).
            new ToolCallEvent("s", "save_to_working_memory",
                "key=shared/patrol/calendar-latest, data={" +
                "\"source\":\"calendar-mcp scan\", \"used\":\"get_emails for reference\"}",
                Succeeded: true, DurationMs: 21,
                Timestamp: observedAt.AddMilliseconds(50)),
        ]);
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        Assert.AreEqual(0, wm.DeletedKeys.Count,
            "SaveToWorkingMemory is not an MCP invocation. Its data blob may " +
            "mention server and tool names but that is not contradicting evidence.");
    }

    [TestMethod]
    public async Task BuildAsync_McpCallWithSubstringButNoStructuredArgs_NotContradiction()
    {
        // Defense against future cases where ToolName happens to be mcp_invoke_tool
        // but the args summary lacks the structured server_name=X, tool_name=Y
        // form. Substring containment alone in a free-form args string must not
        // count as evidence.
        var observedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var stale = ObservationEntry(
            "patrol/email-triage-latest",
            "calendar-mcp/search_emails wrapper cannot pass arguments",
            observedAt);

        var wm = new RecordingWorkingMemory(patrol: [stale]);
        var log = new StubToolCallLog(
        [
            // Looks like an MCP call but args don't carry the structured form.
            new ToolCallEvent("s", "mcp_invoke_tool",
                "the message mentioned calendar-mcp and search_emails but not as call args",
                Succeeded: true, DurationMs: 1,
                Timestamp: observedAt.AddMinutes(30)),
        ]);
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        Assert.AreEqual(0, wm.DeletedKeys.Count,
            "Without the structured server_name=/tool_name= form, an " +
            "mcp_invoke_tool args summary is not evidence of invocation.");
    }

    [TestMethod]
    public async Task BuildAsync_SharedNamespace_AlsoFiltered()
    {
        // Observations under shared/ must be evicted on the same rules as patrol/
        // because shared entries are injected into every session.
        var observedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var stale = ObservationEntry(
            "shared/pending/email-triage-latest",
            "calendar-mcp/search_emails wrapper cannot pass arguments",
            observedAt);

        var wm = new RecordingWorkingMemory(shared: [stale]);
        var log = new StubToolCallLog(
        [
            new ToolCallEvent("s", "mcp_invoke_tool",
                "server_name=calendar-mcp, tool_name=search_emails",
                Succeeded: true, DurationMs: 1,
                Timestamp: observedAt.AddMinutes(30))
        ]);
        var builder = NewBuilder(wm, log);

        await builder.BuildAsync("session-1", LongMessage, CancellationToken.None);

        CollectionAssert.Contains(wm.DeletedKeys, "shared/pending/email-triage-latest");
    }

    // --- helpers -------------------------------------------------------------

    private static WorkingMemoryEntry ObservationEntry(string key, string content, DateTimeOffset storedAt) =>
        new(
            Key: key,
            Value: content,
            StoredAt: storedAt,
            ExpiresAt: storedAt.AddHours(24),
            Category: "patrol/email",
            Tags: [ObservationLanguageDetector.ObservationTag, "heartbeat"]);

    private static AgentContextBuilder NewBuilder(
        RecordingWorkingMemory wm,
        IToolCallLog? toolCallLog)
    {
        var profileHolder = new ProfileHolder();
        var doc = new AgentProfileDocument("soul", null, [], "Test agent.");
        profileHolder.Update(new AgentProfile(doc, doc));

        var profileOpts = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-obs-evict-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(profileOpts.Value.BasePath);

        var clock = new AgentClock(
            new ConfigurationBuilder().Build(),
            profileOpts,
            NullLoggerFactory.Instance.CreateLogger<AgentClock>());

        return new AgentContextBuilder(
            profileHolder: profileHolder,
            agent: new AgentIdentity("TestBot"),
            promptBuilder: new DefaultSystemPromptBuilder(profileHolder, new AgentNameHolder(), Options.Create(new AgentProfileOptions())),
            rulesStore: new StubRulesStore(),
            modelBehavior: ModelBehavior.Default,
            conversationMemory: new StubConversationMemory(),
            longTermMemory: new EmptyLongTermMemory(),
            injectedMemoryTracker: new InjectedMemoryTracker(),
            workingMemory: wm,
            skillStore: new StubSkillStore(),
            skillIndexTracker: new SkillIndexTracker(),
            skillRecallTracker: new SkillRecallTracker(),
            clock: clock,
            serviceSearchIndexProviders: [],
            knowledgeGraphProviders: [],
            knowledgeGraphOptions: Options.Create(new KnowledgeGraphOptions()),
            embeddingGenerators: [],
            logger: NullLogger<AgentContextBuilder>.Instance,
            capabilityClaimVerifier: null,
            toolCallLog: toolCallLog);
    }

    /// <summary>
    /// Working-memory stub that returns predetermined lists for the namespaces
    /// the builder queries (own-namespace, patrol/, shared/, subagent/) and
    /// records which keys were deleted.
    /// </summary>
    private sealed class RecordingWorkingMemory(
        IReadOnlyList<WorkingMemoryEntry>? own = null,
        IReadOnlyList<WorkingMemoryEntry>? patrol = null,
        IReadOnlyList<WorkingMemoryEntry>? shared = null,
        IReadOnlyList<WorkingMemoryEntry>? subagent = null) : IWorkingMemory
    {
        private readonly IReadOnlyList<WorkingMemoryEntry> _own = own ?? [];
        private readonly IReadOnlyList<WorkingMemoryEntry> _patrol = patrol ?? [];
        private readonly IReadOnlyList<WorkingMemoryEntry> _shared = shared ?? [];
        private readonly IReadOnlyList<WorkingMemoryEntry> _subagent = subagent ?? [];
        public List<string> DeletedKeys { get; } = new();

        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null) =>
            Task.CompletedTask;

        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(
            _own.Concat(_patrol).Concat(_shared).Concat(_subagent)
                .FirstOrDefault(e => e.Key == key)?.Value);

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null)
        {
            if (string.IsNullOrEmpty(prefix))
                return Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>(
                    _own.Concat(_patrol).Concat(_shared).Concat(_subagent).ToList());
            if (prefix.Equals("patrol", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(_patrol);
            if (prefix.Equals("shared", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(_shared);
            if (prefix.Equals("subagent", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(_subagent);
            return Task.FromResult(_own);
        }

        public Task DeleteAsync(string key)
        {
            DeletedKeys.Add(key);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }

    private sealed class StubToolCallLog(IReadOnlyList<ToolCallEvent> events) : IToolCallLog
    {
        public Task AppendAsync(ToolCallEvent evt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolCallEvent>>(
                events.Where(e => e.SessionId == sessionId).ToList());

        public Task<IReadOnlyList<ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolCallEvent>>(
                events.Where(e => e.Timestamp >= since).Take(maxResults).ToList());
    }

    private sealed class ThrowingToolCallLog : IToolCallLog
    {
        public Task AppendAsync(ToolCallEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
            throw new InvalidOperationException("log offline");
        public Task<IReadOnlyList<ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            throw new InvalidOperationException("log offline");
    }

    private sealed class EmptyLongTermMemory : ILongTermMemory
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

    private sealed class StubConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
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

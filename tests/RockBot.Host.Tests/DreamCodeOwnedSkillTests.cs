using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Tools;

namespace RockBot.Host.Tests;

/// <summary>
/// No dream pass may create, rewrite, merge or delete a code-owned tool guide (#699). The guides
/// are hidden from the LLM, anything it proposes for them anyway is dropped, and wisp-failure
/// lessons about a guide land in its <c>{name}-learned</c> companion.
/// </summary>
[TestClass]
public class DreamCodeOwnedSkillTests
{
    private static readonly DateTimeOffset Old = DateTimeOffset.UtcNow.AddDays(-30);

    // ── Consolidation ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Consolidation_HidesProtectedSkillsFromThePrompt()
    {
        var store = await StoreWith(
            Guide("memory"),
            Plain("mcp", "an unflagged copy of a registered guide"),
            Plain("plan-meeting", "Plan a meeting."),
            Plain("schedule-meeting", "Schedule a meeting."));
        var llm = new ScriptedLlmClient("""{ "toDelete": [], "toSave": [] }""");

        await CreateService(store, llm, providers: ["memory", "mcp"]).ConsolidateSkillsAsync(CancellationToken.None);

        Assert.AreEqual(1, llm.Calls.Count);
        StringAssert.Contains(llm.Calls[0], "[NAME:plan-meeting]");
        Assert.IsFalse(llm.Calls[0].Contains("[NAME:memory]"), "flagged guide is hidden");
        Assert.IsFalse(llm.Calls[0].Contains("[NAME:mcp]"), "registered guide name is hidden even unflagged");
    }

    [TestMethod]
    public async Task Consolidation_RefusesEveryChangeToAProtectedSkill_AndAppliesTheRest()
    {
        var memory = Guide("memory");
        var mcp = Guide("mcp");
        var store = await StoreWith(
            memory, mcp,
            Plain("plan-meeting", "Plan a meeting."),
            Plain("schedule-meeting", "Schedule a meeting."),
            Plain("memory-notes", "Notes about memory."));
        var llm = new ScriptedLlmClient("""
            {
              "toDelete": ["memory", "mcp", "plan-meeting", "schedule-meeting", "memory-notes"],
              "toSave": [
                { "name": "meeting", "summary": "Meetings", "content": "# Meetings",
                  "sourceNames": ["plan-meeting", "schedule-meeting", "mcp"] },
                { "name": "Memory", "summary": "Merged memory", "content": "# Merged memory",
                  "sourceNames": ["memory", "memory-notes"] }
              ]
            }
            """);

        await CreateService(store, llm, providers: ["memory", "mcp"]).ConsolidateSkillsAsync(CancellationToken.None);

        Assert.AreSame(memory, await store.GetAsync("memory"), "a save under a protected name is dropped");
        Assert.AreSame(mcp, await store.GetAsync("mcp"), "protected names leave toDelete and sourceNames");
        Assert.AreEqual("# Meetings", (await store.GetAsync("meeting"))!.Content);
        Assert.IsNull(await store.GetAsync("plan-meeting"));
        Assert.IsNull(await store.GetAsync("schedule-meeting"));
        Assert.IsNotNull(await store.GetAsync("memory-notes"),
            "a source only the refused save consumed keeps its skill");
    }

    [TestMethod]
    public async Task Consolidation_OnlyProtectedSaves_TripsTheNoReplacementGuard()
    {
        var store = await StoreWith(
            Guide("memory"),
            Plain("memory-notes", "Notes about memory."),
            Plain("other-notes", "Other notes."));
        var llm = new ScriptedLlmClient("""
            {
              "toDelete": ["memory-notes", "other-notes"],
              "toSave": [ { "name": "memory", "content": "# Merged", "sourceNames": ["memory"] } ]
            }
            """);

        await CreateService(store, llm, providers: ["memory"]).ConsolidateSkillsAsync(CancellationToken.None);

        Assert.IsNotNull(await store.GetAsync("memory-notes"));
        Assert.IsNotNull(await store.GetAsync("other-notes"));
    }

    [TestMethod]
    public async Task Consolidation_PruneSkipsProtectedSkills()
    {
        var longAgo = DateTimeOffset.UtcNow.AddYears(-2);
        var memory = new Skill("memory", "s", "# Memory guide", longAgo, longAgo, longAgo, CodeOwned: true);
        var store = await StoreWith(memory, new Skill("stale", "s", "body", longAgo, longAgo, longAgo));
        var llm = new ScriptedLlmClient("""{ "toDelete": [], "toSave": [] }""");

        await CreateService(store, llm, providers: ["memory"]).ConsolidateSkillsAsync(CancellationToken.None);

        Assert.AreSame(memory, await store.GetAsync("memory"));
        Assert.IsNull(await store.GetAsync("stale"));
    }

    // ── Optimization ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Optimize_NeverRewritesOrDeletesAProtectedSkill()
    {
        var memory = Guide("memory");
        var web = Guide("web", "short");   // sparse, and old enough to be offered for expansion
        var store = await StoreWith(memory, web, Plain("plan-meeting", "Plan a meeting."));
        var usage = new StubSkillUsageStore(
            new SkillInvocationEvent("e1", "memory", "s1", DateTimeOffset.UtcNow.AddHours(-2)),
            new SkillInvocationEvent("e2", "plan-meeting", "s1", DateTimeOffset.UtcNow.AddHours(-2)));
        var feedback = new StubFeedbackStore(
            new FeedbackEntry("f1", "s1", FeedbackSignalType.Correction, "wrong", null, DateTimeOffset.UtcNow.AddHours(-1)));
        var llm = new ScriptedLlmClient("""
            {
              "toDelete": ["memory", "web", "plan-meeting"],
              "toSave": [
                { "name": "memory", "content": "# Improved memory", "sourceNames": ["memory"] },
                { "name": "plan-meeting", "content": "# Improved plan", "sourceNames": ["plan-meeting"] }
              ]
            }
            """);

        var service = CreateService(store, llm, providers: ["memory", "web"], usage: usage, feedback: feedback);
        await service.OptimizeSkillsAsync(CancellationToken.None);

        Assert.AreEqual(1, llm.Calls.Count);
        StringAssert.Contains(llm.Calls[0], "## Skill: plan-meeting");
        Assert.IsFalse(llm.Calls[0].Contains("## Skill: memory"), "at-risk guide is not offered");
        Assert.IsFalse(llm.Calls[0].Contains("## Skill: web"), "sparse guide is not offered");
        Assert.AreSame(memory, await store.GetAsync("memory"));
        Assert.AreSame(web, await store.GetAsync("web"));
        Assert.AreEqual("# Improved plan", (await store.GetAsync("plan-meeting"))!.Content);
    }

    // ── Skill gap and sequence detection ─────────────────────────────────────

    [TestMethod]
    public async Task SkillGap_DoesNotCreateOrOverwriteAProtectedName()
    {
        var memory = Guide("memory");
        var store = await StoreWith(memory);
        var log = new StubConversationLog(
            new ConversationLogEntry("s1", "user", "summarize my week", DateTimeOffset.UtcNow));
        var llm = new ScriptedLlmClient("""
            {
              "toSave": [
                { "name": "memory", "summary": "s", "content": "# Overwrite" },
                { "name": " mcp ", "summary": "s", "content": "# Take the name" },
                { "name": "weekly-summary", "summary": "s", "content": "# Weekly summary" }
              ]
            }
            """);

        await CreateService(store, llm, providers: ["memory", "mcp"], conversationLog: log)
            .RunSkillGapDetectionPassAsync(CancellationToken.None);

        Assert.AreSame(memory, await store.GetAsync("memory"));
        Assert.IsNull(await store.GetAsync("mcp"), "a registered guide's name is reserved even while absent");
        Assert.IsNotNull(await store.GetAsync("weekly-summary"));
    }

    [TestMethod]
    public async Task SequenceDetection_SkipsProtectedNames_AndMatchesExistingSkillsByTrimmedName()
    {
        var web = Guide("web");
        var existing = Plain("fetch-and-summarize", "Existing procedure.");
        var store = await StoreWith(web, existing);
        var llm = new ScriptedLlmClient("""
            {
              "toSave": [
                { "name": " web ", "summary": "s", "content": "# Overwrite" },
                { "name": "mcp", "summary": "s", "content": "# Take the name" },
                { "name": " fetch-and-summarize ", "summary": "s", "content": "# Duplicate" },
                { "name": "search-then-save", "summary": "s", "content": "# New sequence" }
              ]
            }
            """);

        await CreateService(store, llm, providers: ["web", "mcp"], toolCallLog: new StubToolCallLog(ToolCalls()))
            .RunSequenceSkillDetectionPassAsync(CancellationToken.None);

        Assert.AreSame(web, await store.GetAsync("web"));
        Assert.IsNull(await store.GetAsync("mcp"));
        Assert.AreSame(existing, await store.GetAsync("fetch-and-summarize"));
        Assert.AreEqual("# New sequence", (await store.GetAsync("search-then-save"))!.Content);
    }

    // ── Wisp failure analysis ────────────────────────────────────────────────

    [TestMethod]
    public async Task WispFailure_LessonAboutAGuide_LandsInItsCompanion()
    {
        var mcp = Guide("mcp");
        var store = await StoreWith(mcp, Plain("plan-meeting", "Plan a meeting."));
        var llm = new ScriptedLlmClient("""
            {
              "patterns": [],
              "skillUpdates": [
                { "name": "MCP", "annotation": "Pass the server name explicitly." },
                { "name": "plan-meeting", "annotation": "Check time zones." }
              ]
            }
            """);

        var service = CreateService(store, llm, providers: ["mcp"], wispLog: new StubWispLog(WispRecords()));
        await service.RunWispFailureAnalysisPassAsync(CancellationToken.None);

        Assert.AreSame(mcp, await store.GetAsync("mcp"));
        var companion = (await store.GetAsync("mcp-learned"))!;
        Assert.IsNotNull(companion);
        Assert.IsFalse(companion.CodeOwned);
        CollectionAssert.AreEqual(new[] { "mcp" }, companion.SeeAlso!.ToArray());
        StringAssert.Contains(companion.Content, "get_tool_guide(\"mcp\")");
        StringAssert.Contains(companion.Content, "## Wisp Failure Pattern\n\nPass the server name explicitly.");
        StringAssert.Contains((await store.GetAsync("plan-meeting"))!.Content, "Check time zones.");

        // A second lesson appends to the existing companion.
        await service.RunWispFailureAnalysisPassAsync(CancellationToken.None);
        var again = (await store.GetAsync("mcp-learned"))!;
        Assert.AreEqual(2, again.Content.Split("## Wisp Failure Pattern").Length - 1);
        CollectionAssert.AreEqual(new[] { "mcp" }, again.SeeAlso!.ToArray());
    }

    // ── Change-gate fingerprint ──────────────────────────────────────────────

    [TestMethod]
    public void Fingerprint_ChangesWhenTheProtectedSetChanges()
    {
        IReadOnlyList<Skill> skills = [Plain("plan-meeting", "Plan a meeting.")];

        var none = DreamService.SkillCorpusFingerprint(skills, []);
        var one = DreamService.SkillCorpusFingerprint(skills, [], ["memory"]);
        var two = DreamService.SkillCorpusFingerprint(skills, [], ["memory", "web"]);

        Assert.AreEqual(none, DreamService.SkillCorpusFingerprint(skills, [], []));
        Assert.AreNotEqual(none, one);
        Assert.AreNotEqual(one, two);
        Assert.AreEqual(two, DreamService.SkillCorpusFingerprint(skills, [], ["WEB", "Memory"]), "order and case don't matter");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Skill Guide(string name, string? content = null) =>
        new(name, $"{name} guide", content ?? $"# {name} guide\n\n" + new string('x', 300), Old, Old, CodeOwned: true);

    private static Skill Plain(string name, string content) => new(name, "summary", content, Old, Old);

    private static async Task<SkillBodyApplierTests.InMemorySkillStore> StoreWith(params Skill[] skills)
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        foreach (var skill in skills)
            await store.SaveAsync(skill);
        return store;
    }

    private static List<ToolCallEvent> ToolCalls()
    {
        var events = new List<ToolCallEvent>();
        for (var session = 0; session < 4; session++)
            for (var call = 0; call < 3; call++)
                events.Add(new ToolCallEvent($"s{session}", call == 0 ? "web_search" : "web_browse", null, true, 10,
                    DateTimeOffset.UtcNow.AddMinutes(-60 + session * 10 + call)));
        return events;
    }

    private static List<WispExecutionRecord> WispRecords() =>
        Enumerable.Range(0, 6).Select(i => new WispExecutionRecord
        {
            WispId = $"w{i}",
            Description = "list calendar accounts",
            DefinitionHash = "hash",
            Succeeded = false,
            StepCount = 2,
            StepsCompleted = 1,
            FailedStepId = "list",
            FailureCategory = "Structural",
            ErrorMessage = "server not found",
            DurationMs = 50,
            Timestamp = DateTimeOffset.UtcNow.AddHours(-i - 1)
        }).ToList();

    private static DreamService CreateService(
        ISkillStore store,
        ILlmClient llm,
        string[] providers,
        ISkillUsageStore? usage = null,
        IFeedbackStore? feedback = null,
        IConversationLog? conversationLog = null,
        IToolCallLog? toolCallLog = null,
        IWispExecutionLog? wispLog = null)
    {
        var service = new DreamService(
            new EmptyLongTermMemory(),
            [store],
            llm,
            new AgentWorkSerializer(),
            new StubActivityMonitor(),
            new AgentClock(
                new ConfigurationBuilder().Build(),
                Options.Create(new AgentProfileOptions()),
                NullLogger<AgentClock>.Instance),
            Options.Create(new DreamOptions { Enabled = false }),
            Options.Create(new AgentProfileOptions()),
            NullLogger<DreamService>.Instance,
            feedbackStore: feedback,
            skillUsageStore: usage,
            conversationLog: conversationLog,
            toolCallLog: toolCallLog,
            wispExecutionLog: wispLog,
            toolSkillProviders: providers.Select(p => new StarterSkillServiceTests.StubProvider(p, $"{p} tools", $"# {p} guide")));
        service.UseBuiltInSkillDirectives();
        return service;
    }

    private sealed class ScriptedLlmClient(params string[] responses) : ILlmClient
    {
        public List<string> Calls { get; } = [];
        public bool IsIdle => true;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(string.Join("\n", messages.Select(m => m.Text)));
            var index = Math.Min(Calls.Count - 1, responses.Length - 1);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responses[index])));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetResponseAsync(messages, options, cancellationToken);
    }

    private sealed class StubSkillUsageStore(params SkillInvocationEvent[] events) : ISkillUsageStore
    {
        public Task AppendAsync(SkillInvocationEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SkillInvocationEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SkillInvocationEvent>>(events.Where(e => e.SessionId == sessionId).ToList());
        public Task<IReadOnlyList<SkillInvocationEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SkillInvocationEvent>>(events);
    }

    private sealed class StubFeedbackStore(params FeedbackEntry[] entries) : IFeedbackStore
    {
        public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>(entries.Where(e => e.SessionId == sessionId).ToList());
        public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>(entries);
    }

    private sealed class StubConversationLog(params ConversationLogEntry[] entries) : IConversationLog
    {
        public Task AppendAsync(ConversationLogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationLogEntry>> ReadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationLogEntry>>(entries);
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubToolCallLog(IReadOnlyList<ToolCallEvent> events) : IToolCallLog
    {
        public Task AppendAsync(ToolCallEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolCallEvent>>(events.Where(e => e.SessionId == sessionId).ToList());
        public Task<IReadOnlyList<ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            Task.FromResult(events);
    }

    private sealed class StubWispLog(IReadOnlyList<WispExecutionRecord> records) : IWispExecutionLog
    {
        public Task AppendAsync(WispExecutionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WispExecutionRecord>> QueryRecentAsync(
            DateTimeOffset since, int maxResults, CancellationToken ct = default) => Task.FromResult(records);
        public Task<WispExecutionRecord?> FindRecentFailureAsync(
            string definitionHash, string? sessionId, CancellationToken ct = default) =>
            Task.FromResult<WispExecutionRecord?>(null);
    }

    private sealed class StubActivityMonitor : IUserActivityMonitor
    {
        public void RecordActivity() { }
        public bool IsUserActive(TimeSpan idleThreshold) => false;
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
}

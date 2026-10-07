using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Memory;

namespace RockBot.Host.Tests;

/// <summary>
/// Host-side skill freshness (#615): which writes record a new MCP surface baseline (repair
/// tickets, the dream refresh pass), which carry the existing one (dream merges), that the store
/// round-trips it, and that the dream pass refreshes only stale <c>mcp/</c> skills.
/// </summary>
[TestClass]
public class McpSkillFreshnessHostTests
{
    private static readonly SkillSurfaceBaseline OldBaseline =
        new("adjutant", "id-a", "fp-old", "1.0.0", "ident-a", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly SkillSurfaceBaseline NewBaseline =
        new("adjutant", "id-a", "fp-new", "1.1.0", "ident-a", new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero));

    // ── Repair tickets ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task SkillBodyApplier_McpSkill_ReBaselines_AndRevertRestoresTheOldBaseline()
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        await store.SaveAsync(new Skill("mcp/adjutant", "s", "Original.", DateTimeOffset.UtcNow, SurfaceBaseline: OldBaseline));
        var applier = new SkillBodyApplier(store, NullLogger<SkillBodyApplier>.Instance,
            new StubSkillSurface { Capture = SkillBaselineCapture.Recorded(NewBaseline) });

        var outcome = await applier.ApplyAsync(Ticket("mcp/adjutant"), CancellationToken.None);
        Assert.AreEqual(NewBaseline, (await store.GetAsync("mcp/adjutant"))!.SurfaceBaseline);

        await outcome.Revert!(CancellationToken.None);
        var reverted = (await store.GetAsync("mcp/adjutant"))!;
        Assert.AreEqual("Original.", reverted.Content);
        Assert.AreEqual(OldBaseline, reverted.SurfaceBaseline);
    }

    [TestMethod]
    public async Task SkillBodyApplier_UnreadableSurface_ClearsTheBaseline()
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        await store.SaveAsync(new Skill("mcp/adjutant", "s", "Original.", DateTimeOffset.UtcNow, SurfaceBaseline: OldBaseline));
        var applier = new SkillBodyApplier(store, NullLogger<SkillBodyApplier>.Instance,
            new StubSkillSurface { Capture = SkillBaselineCapture.NotRecorded("not connected") });

        await applier.ApplyAsync(Ticket("mcp/adjutant"), CancellationToken.None);

        Assert.IsNull((await store.GetAsync("mcp/adjutant"))!.SurfaceBaseline);
    }

    [TestMethod]
    public async Task SkillBodyApplier_NonMcpSkill_KeepsWhateverItHad()
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        await store.SaveAsync(new Skill("calendar/foo", "s", "Original.", DateTimeOffset.UtcNow));
        var surface = new StubSkillSurface { Capture = SkillBaselineCapture.Recorded(NewBaseline) };
        var applier = new SkillBodyApplier(store, NullLogger<SkillBodyApplier>.Instance, surface);

        await applier.ApplyAsync(Ticket("calendar/foo"), CancellationToken.None);

        Assert.IsNull((await store.GetAsync("calendar/foo"))!.SurfaceBaseline);
        Assert.AreEqual(0, surface.CaptureCalls);
    }

    // ── FileSkillStore ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task FileSkillStore_RoundTripsTheBaseline_AndReadsOldFilesAsUnknown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rockbot-skill-baseline-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A file written before #615 has no surfaceBaseline property at all.
            Directory.CreateDirectory(Path.Combine(dir, "mcp"));
            await File.WriteAllTextAsync(Path.Combine(dir, "mcp", "legacy.json"), """
                { "name": "mcp/legacy", "summary": "s", "content": "body", "createdAt": "2026-01-01T00:00:00+00:00" }
                """);

            var store = new FileSkillStore(
                Options.Create(new SkillOptions { BasePath = dir }),
                Options.Create(new AgentProfileOptions()),
                Options.Create(new EmbeddingOptions()),
                NullLogger<FileSkillStore>.Instance,
                EmbeddingTextPreparer.ForTests());

            await store.SaveAsync(new Skill("mcp/adjutant", "s", "body", DateTimeOffset.UtcNow, SurfaceBaseline: NewBaseline));
            Assert.AreEqual(NewBaseline, (await store.GetAsync("mcp/adjutant"))!.SurfaceBaseline);

            var loaded = await store.GetAsync("mcp/legacy");
            Assert.IsNotNull(loaded);
            Assert.IsNull(loaded!.SurfaceBaseline);

            // Edits keep it (content edits by the agent re-baseline in SkillTools, not here).
            await store.EditContentAsync("mcp/adjutant", "body", "new body");
            Assert.AreEqual(NewBaseline, (await store.GetAsync("mcp/adjutant"))!.SurfaceBaseline);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ── Dream: carrying baselines through merges ─────────────────────────────

    [TestMethod]
    public void CarriedBaseline_OldestOfTheSameServerWins()
    {
        var baselines = new Dictionary<string, SkillSurfaceBaseline?>(StringComparer.OrdinalIgnoreCase)
        {
            ["mcp/adjutant"] = NewBaseline,
            ["mcp/adjutant/calendar"] = OldBaseline,
            ["mcp/ms365"] = OldBaseline with { ServerName = "ms365", RecordedAt = DateTimeOffset.MinValue }
        };

        Assert.AreEqual(OldBaseline,
            DreamService.CarriedSurfaceBaseline("mcp/adjutant", ["mcp/adjutant/calendar", "mcp/ms365"], baselines));
    }

    [TestMethod]
    public void CarriedBaseline_AnySourceWithoutABaseline_MakesTheMergeUnknown()
    {
        var baselines = new Dictionary<string, SkillSurfaceBaseline?>(StringComparer.OrdinalIgnoreCase)
        {
            ["mcp/adjutant"] = NewBaseline,
            ["mcp/adjutant/legacy"] = null
        };

        Assert.IsNull(DreamService.CarriedSurfaceBaseline("mcp/adjutant", ["mcp/adjutant/legacy"], baselines));
    }

    [TestMethod]
    public void CarriedBaseline_NonMcpTarget_HasNone()
    {
        var baselines = new Dictionary<string, SkillSurfaceBaseline?> { ["mcp/adjutant"] = NewBaseline };
        Assert.IsNull(DreamService.CarriedSurfaceBaseline("calendar-howto", ["mcp/adjutant"], baselines));
    }

    // ── Dream: the refresh pass ──────────────────────────────────────────────

    [TestMethod]
    public async Task RefreshPass_RewritesOnlyStaleSkills_AndRecordsTheNewBaseline()
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        await store.SaveAsync(Skill("mcp/adjutant", "Call get_events with `start`.", OldBaseline));
        await store.SaveAsync(Skill("mcp/ms365", "fresh body", OldBaseline with { ServerName = "ms365" }));
        await store.SaveAsync(Skill("mcp/legacy", "unknown body", null));
        await store.SaveAsync(Skill("plan-meeting", "not mcp", null));

        var surface = new StubSkillSurface
        {
            Capture = SkillBaselineCapture.Recorded(NewBaseline),
            Statuses = { ["mcp/adjutant"] = SkillFreshnessStatus.Stale, ["mcp/ms365"] = SkillFreshnessStatus.Fresh }
        };
        var llm = new ScriptedLlmClient("""{ "changed": true, "content": "Call get_events with `from`.", "notes": "start renamed to from" }""");

        await CreateService(store, surface, llm).RunMcpSkillRefreshPassAsync(CancellationToken.None);

        Assert.AreEqual(1, llm.Calls.Count, "one LLM call, for the one stale skill");
        StringAssert.Contains(llm.Calls[0], "### get_events");
        StringAssert.Contains(llm.Calls[0], "Call get_events with `start`.");

        var refreshed = (await store.GetAsync("mcp/adjutant"))!;
        Assert.AreEqual("Call get_events with `from`.", refreshed.Content);
        Assert.AreEqual(NewBaseline, refreshed.SurfaceBaseline);

        Assert.AreEqual("fresh body", (await store.GetAsync("mcp/ms365"))!.Content);
        Assert.IsNull((await store.GetAsync("mcp/legacy"))!.SurfaceBaseline, "unknown skills are left alone");
    }

    [TestMethod]
    public async Task RefreshPass_UnchangedVerdict_KeepsTheContent_ButRecordsTheBaseline()
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        await store.SaveAsync(Skill("mcp/adjutant", "still right", OldBaseline));
        var surface = new StubSkillSurface
        {
            Capture = SkillBaselineCapture.Recorded(NewBaseline),
            Statuses = { ["mcp/adjutant"] = SkillFreshnessStatus.Stale }
        };
        var llm = new ScriptedLlmClient("""{ "changed": false, "content": "", "notes": "only descriptions changed" }""");

        await CreateService(store, surface, llm).RunMcpSkillRefreshPassAsync(CancellationToken.None);

        var skill = (await store.GetAsync("mcp/adjutant"))!;
        Assert.AreEqual("still right", skill.Content);
        Assert.AreEqual(NewBaseline, skill.SurfaceBaseline);
    }

    [TestMethod]
    public async Task RefreshPass_RespectsTheCap_OldestFirst()
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        var surface = new StubSkillSurface { Capture = SkillBaselineCapture.Recorded(NewBaseline) };
        for (var i = 0; i < 3; i++)
        {
            var name = $"mcp/adjutant/s{i}";
            await store.SaveAsync(Skill(name, $"body {i}", OldBaseline, updatedDaysAgo: 10 - i));
            surface.Statuses[name] = SkillFreshnessStatus.Stale;
        }
        var llm = new ScriptedLlmClient("""{ "changed": true, "content": "rewritten", "notes": "n" }""");

        await CreateService(store, surface, llm, new DreamOptions { Enabled = false, McpSkillRefreshMaxPerCycle = 2 })
            .RunMcpSkillRefreshPassAsync(CancellationToken.None);

        Assert.AreEqual(2, llm.Calls.Count);
        Assert.AreEqual("rewritten", (await store.GetAsync("mcp/adjutant/s0"))!.Content);
        Assert.AreEqual("rewritten", (await store.GetAsync("mcp/adjutant/s1"))!.Content);
        Assert.AreEqual("body 2", (await store.GetAsync("mcp/adjutant/s2"))!.Content, "newest waits for the next cycle");
    }

    [TestMethod]
    public async Task RefreshPass_UnreadableSurface_LeavesTheSkillStale_WithoutAnLlmCall()
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        await store.SaveAsync(Skill("mcp/adjutant", "old body", OldBaseline));
        var surface = new StubSkillSurface
        {
            Capture = SkillBaselineCapture.NotRecorded("not connected"),
            Statuses = { ["mcp/adjutant"] = SkillFreshnessStatus.Stale }
        };
        var llm = new ScriptedLlmClient("""{ "changed": true, "content": "x" }""");

        await CreateService(store, surface, llm).RunMcpSkillRefreshPassAsync(CancellationToken.None);

        Assert.AreEqual(0, llm.Calls.Count);
        Assert.AreEqual(OldBaseline, (await store.GetAsync("mcp/adjutant"))!.SurfaceBaseline);
    }

    [TestMethod]
    public async Task RefreshPass_Disabled_DoesNothing()
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        await store.SaveAsync(Skill("mcp/adjutant", "old body", OldBaseline));
        var surface = new StubSkillSurface
        {
            Capture = SkillBaselineCapture.Recorded(NewBaseline),
            Statuses = { ["mcp/adjutant"] = SkillFreshnessStatus.Stale }
        };
        var llm = new ScriptedLlmClient("""{ "changed": true, "content": "x" }""");

        await CreateService(store, surface, llm, new DreamOptions { Enabled = false, McpSkillRefreshEnabled = false })
            .RunMcpSkillRefreshPassAsync(CancellationToken.None);

        Assert.AreEqual(0, llm.Calls.Count);
    }

    [TestMethod]
    public async Task ApplyRefresh_SkillWrittenMeanwhile_IsLeftForTheNextCycle()
    {
        var store = new SkillBodyApplierTests.InMemorySkillStore();
        var original = Skill("mcp/adjutant", "old body", OldBaseline);
        await store.SaveAsync(original with { Content = "edited by the agent", UpdatedAt = DateTimeOffset.UtcNow });

        var applied = await DreamService.ApplyMcpSkillRefreshAsync(
            store, original,
            new DreamService.McpSkillRefreshResultDto { Changed = true, Content = "dream rewrite" },
            NewBaseline, NullLogger.Instance);

        Assert.IsFalse(applied);
        Assert.AreEqual("edited by the agent", (await store.GetAsync("mcp/adjutant"))!.Content);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Skill Skill(string name, string content, SkillSurfaceBaseline? baseline, int updatedDaysAgo = 5) =>
        new(name, "summary", content, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-updatedDaysAgo),
            SurfaceBaseline: baseline);

    private static RepairTicket Ticket(string skill) =>
        new(Id: "t-1",
            PatternKey: "p|q|r",
            Target: RepairTarget.SkillBody,
            Change: JsonDocument.Parse($$"""{ "skill": "{{skill}}", "ops": [ { "op": "append", "text": "Appended." } ] }""").RootElement,
            Verify: new VerifyShape("svr", "tool", JsonDocument.Parse("{}").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success)),
            Attempts: [],
            Status: RepairStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static DreamService CreateService(
        ISkillStore store, IMcpSkillSurface surface, ILlmClient llm, DreamOptions? options = null) =>
        new(new EmptyLongTermMemory(),
            [store],
            llm,
            new AgentWorkSerializer(),
            new StubActivityMonitor(),
            new AgentClock(
                new ConfigurationBuilder().Build(),
                Options.Create(new AgentProfileOptions()),
                NullLogger<AgentClock>.Instance),
            Options.Create(options ?? new DreamOptions { Enabled = false }),
            Options.Create(new AgentProfileOptions()),
            NullLogger<DreamService>.Instance,
            mcpSkillSurface: surface);

    private sealed class StubSkillSurface : IMcpSkillSurface
    {
        public SkillBaselineCapture Capture { get; set; } = SkillBaselineCapture.NotRecorded("not configured");
        public Dictionary<string, SkillFreshnessStatus> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int CaptureCalls { get; private set; }

        public SkillBaselineCapture CaptureBaseline(string skillName)
        {
            CaptureCalls++;
            return Capture;
        }

        public SkillFreshness Evaluate(Skill skill) =>
            new(Statuses.GetValueOrDefault(skill.Name, SkillFreshnessStatus.Unknown), "adjutant's tool surface changed since this skill was written");

        public Task<string?> GetLiveSurfaceTextAsync(string serverName, CancellationToken ct) =>
            Task.FromResult<string?>("### get_events\nLists events.\nInput schema: {\"properties\":{\"from\":{}}}");
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

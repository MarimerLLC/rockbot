using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// #848: the dream's scheduled-task efficiency pass. Selection is deterministic
/// (<see cref="ScheduledTaskEfficiencyAdvisor"/>); the pass shows the model what the runs did and
/// applies its proposal: a skill rewrite, a directive, a saved wisp, and a gate, switched on only
/// when its wisp only reads.
/// </summary>
[TestClass]
public sealed class DreamScheduledTaskEfficiencyTests
{
    private const string TaskName = "concert-sweep";
    private const string SkillName = "concert-email-sweep";
    private static readonly ScheduledTaskEfficiencyAdvisor.Thresholds Defaults = new();
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private string _dir = "";

    [TestInitialize]
    public void Setup() => _dir = Path.Combine(Path.GetTempPath(), $"rb-efficiency-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private static ScheduledTask Weekly(bool system = false, bool runOnce = false) =>
        new(TaskName, "0 9 * * 1", "Sweep my mail for concert tickets and add them to my calendar.", T0.AddDays(-60),
            IsSystemTask: system, RunOnce: runOnce);

    private static ScheduledRunRecord Run(int week, double cost = 1.5, string? hash = "same",
        ScheduledRunOutcome outcome = ScheduledRunOutcome.Replied, int? toolCalls = 5, int? duplicates = 0) =>
        new(T0.AddDays(7 * week), T0.AddDays(7 * week).AddMinutes(5), cost, ModelTier.Balanced,
            Outcome: outcome, ReplyHash: outcome == ScheduledRunOutcome.Replied ? hash : null,
            ToolCalls: toolCalls, DuplicateFetches: duplicates);

    [TestMethod]
    public void Select_SameReportThreeTimes_IsACandidate()
    {
        var candidate = ScheduledTaskEfficiencyAdvisor.Select(Weekly(), [Run(0), Run(1), Run(2)], null, Defaults);

        Assert.IsNotNull(candidate);
        Assert.AreEqual(1.5, candidate.MedianCostUsd, 1e-9);
        Assert.AreEqual(3, candidate.Runs.Count);
        StringAssert.Contains(candidate.Signals.Single(), "3 of 3 runs sent the same report");
    }

    [TestMethod]
    public void Select_EmptyRunsCountWithSameReports()
    {
        var candidate = ScheduledTaskEfficiencyAdvisor.Select(Weekly(),
            [Run(0, hash: "a"), Run(1, outcome: ScheduledRunOutcome.Empty), Run(2, outcome: ScheduledRunOutcome.Empty)],
            null, Defaults);

        Assert.IsNotNull(candidate);
        StringAssert.Contains(candidate.Signals.Single(), "reported nothing or the same thing");
    }

    [TestMethod]
    public void Select_DuplicateFetches_OrManyToolCalls_AreSignals()
    {
        var dup = ScheduledTaskEfficiencyAdvisor.Select(Weekly(),
            [Run(0, hash: "a"), Run(1, hash: "b", duplicates: 3), Run(2, hash: "c")], null, Defaults);
        var heavy = ScheduledTaskEfficiencyAdvisor.Select(Weekly(),
            [Run(0, hash: "a", toolCalls: 40), Run(1, hash: "b", toolCalls: 20), Run(2, hash: "c", toolCalls: 2)], null, Defaults);

        StringAssert.Contains(dup!.Signals.Single(), "3 repeated fetch(es) across 1 run(s)");
        StringAssert.Contains(heavy!.Signals.Single(), "a median 20 tool calls per run");
    }

    [TestMethod]
    public void Select_NoSignal_Cheap_TooFewRuns_System_RunOnce_AreSkipped()
    {
        Assert.IsNull(ScheduledTaskEfficiencyAdvisor.Select(Weekly(),
            [Run(0, hash: "a"), Run(1, hash: "b"), Run(2, hash: "c")], null, Defaults), "nothing wasteful");
        Assert.IsNull(ScheduledTaskEfficiencyAdvisor.Select(Weekly(),
            [Run(0, 0.05), Run(1, 0.05), Run(2, 0.05)], null, Defaults), "under the cost floor");
        Assert.IsNull(ScheduledTaskEfficiencyAdvisor.Select(Weekly(), [Run(0), Run(1)], null, Defaults), "two runs");
        Assert.IsNull(ScheduledTaskEfficiencyAdvisor.Select(Weekly(system: true), [Run(0), Run(1), Run(2)], null, Defaults));
        Assert.IsNull(ScheduledTaskEfficiencyAdvisor.Select(Weekly(runOnce: true), [Run(0), Run(1), Run(2)], null, Defaults));
    }

    [TestMethod]
    public void Select_CountsOnlyRunsSinceTheLastOptimization()
    {
        var runs = new[] { Run(0), Run(1), Run(2), Run(3), Run(4) };
        var optimized = new ScheduledTaskOptimization(T0.AddDays(7 * 3 - 1), ["rewrote skill"], 1.5);

        Assert.IsNull(ScheduledTaskEfficiencyAdvisor.Select(Weekly(), runs, optimized, Defaults),
            "only two runs since the change");
        Assert.AreEqual(3, ScheduledTaskEfficiencyAdvisor.Select(Weekly(), [.. runs, Run(5)], optimized, Defaults)!.Runs.Count);
    }

    [TestMethod]
    public void Select_GatedSkipsAreNotWastedRuns()
    {
        var runs = new[]
        {
            Run(0, hash: "a"),
            Run(1, outcome: ScheduledRunOutcome.GatedSkip),
            Run(2, outcome: ScheduledRunOutcome.GatedSkip),
            Run(3, outcome: ScheduledRunOutcome.GatedSkip),
        };

        Assert.IsNull(ScheduledTaskEfficiencyAdvisor.Select(Weekly(), runs, null, Defaults));
    }

    // ── The pass ──────────────────────────────────────────────────────────────

    private const string ReadOnlyWisp = """{"description":"new ticket mail","steps":[{"id":"search","mode":"direct","gateway":"mcp","server":"mail","tool":"search_messages","params":{"after":"{{last_run_date}}"}}]}""";

    [TestMethod]
    public async Task Pass_PromptShowsTheRuns_AndAppliesRewriteDirectiveWispAndReadOnlyGate()
    {
        var (service, llm, store, skills, costs) = await ArrangeAsync($$$"""
            {
              "tasks": [{
                "task": "{{{TaskName}}}",
                "skillRewrite": { "name": "{{{SkillName}}}", "content": "# Sweep\nSearch only mail since {{last_run_date}}." },
                "directive": "Skip: msg-1, msg-2",
                "newWisp": { "skill": "{{{SkillName}}}", "filename": "new-ticket-mail.json", "description": "ticket mail since last run",
                             "body": {{{ReadOnlyWisp}}} },
                "gate": { "skill": "{{{SkillName}}}", "resource": "new-ticket-mail.json", "mode": "nonEmpty" },
                "rationale": "Every run re-read the whole inbox and found nothing new."
              }]
            }
            """);

        await service.RunScheduledTaskEfficiencyPassAsync(CancellationToken.None);

        var prompt = llm.Calls.Single();
        StringAssert.Contains(prompt, $"## Task: {TaskName}");
        StringAssert.Contains(prompt, "runs sent the same report");
        StringAssert.Contains(prompt, "| $1.50 |");
        StringAssert.Contains(prompt, $"### Skill: {SkillName}");
        StringAssert.Contains(prompt, "Read every message in the inbox.");
        StringAssert.Contains(prompt, "- search_messages(query=concert)");
        StringAssert.Contains(prompt, "### Report of");
        StringAssert.Contains(prompt, "Nothing new this week.");

        var skill = (await skills.GetAsync(SkillName))!;
        Assert.AreEqual("# Sweep\nSearch only mail since {{last_run_date}}.", skill.Content);
        var wisp = skill.Manifest!.Single(r => r.Filename == "new-ticket-mail.json");
        Assert.IsTrue(wisp.Provisional, "a wisp the model wrote is validated by its own runs");
        Assert.AreEqual("shape-1", wisp.DefinitionHash);
        Assert.IsTrue(skill.Manifest!.Any(r => r.Filename == "old.json"), "the rewrite keeps the skill's resources");

        var task = (await store.GetAsync(TaskName))!;
        Assert.AreEqual("Skip: msg-1, msg-2", task.Directive);
        Assert.AreEqual(new TaskGate(SkillName, "new-ticket-mail.json", TaskGateMode.NonEmpty,
            EnabledBy: DreamService.OptimizerGateOwner, Reason: "Every run re-read the whole inbox and found nothing new."), task.Gate);

        var optimization = (await costs.GetOptimizationsAsync(TaskName)).Single();
        Assert.AreEqual(1.5, optimization.BaselineMedianUsd!.Value, 1e-9);
        CollectionAssert.AreEqual(new[]
        {
            $"rewrote skill '{SkillName}'",
            "updated the task directive",
            $"saved wisp {SkillName}/new-ticket-mail.json",
            $"gated on wisp {SkillName}/new-ticket-mail.json (nonEmpty)",
        }, optimization.Changes.ToArray());
        Assert.IsNull(optimization.SuggestedGate);
    }

    [TestMethod]
    public async Task Pass_WritingWispGate_IsOnlySuggested()
    {
        var (service, _, store, _, costs) = await ArrangeAsync($$"""
            { "tasks": [{ "task": "{{TaskName}}", "gate": { "skill": "{{SkillName}}", "resource": "writes.json" } }] }
            """);

        await service.RunScheduledTaskEfficiencyPassAsync(CancellationToken.None);

        Assert.IsNull((await store.GetAsync(TaskName))!.Gate);
        var optimization = (await costs.GetOptimizationsAsync(TaskName)).Single();
        Assert.AreEqual($"{SkillName}/writes.json", optimization.SuggestedGate);
        Assert.AreEqual(0, optimization.Changes.Count);
    }

    [TestMethod]
    public async Task Pass_UserSetGate_IsNotReplaced()
    {
        var (service, _, store, _, costs) = await ArrangeAsync($$"""
            { "tasks": [{ "task": "{{TaskName}}", "gate": { "skill": "{{SkillName}}", "resource": "old.json" } }] }
            """);
        var userGate = new TaskGate(SkillName, "mine.json", EnabledBy: "user");
        await store.UpdateGateAsync(TaskName, userGate);

        await service.RunScheduledTaskEfficiencyPassAsync(CancellationToken.None);

        Assert.AreEqual(userGate, (await store.GetAsync(TaskName))!.Gate);
        Assert.AreEqual($"{SkillName}/old.json", (await costs.GetOptimizationsAsync(TaskName)).Single().SuggestedGate);
    }

    [TestMethod]
    public async Task Pass_CodeOwnedOrUnshownSkills_AreNotRewritten()
    {
        var (service, _, _, skills, costs) = await ArrangeAsync($$"""
            { "tasks": [{ "task": "{{TaskName}}",
                          "skillRewrite": { "name": "{{SkillName}}", "content": "# hijacked" } },
                        { "task": "{{TaskName}}",
                          "skillRewrite": { "name": "unrelated", "content": "# hijacked" } }] }
            """, codeOwned: true);

        await service.RunScheduledTaskEfficiencyPassAsync(CancellationToken.None);

        Assert.AreEqual("Read every message in the inbox.", (await skills.GetAsync(SkillName))!.Content);
        Assert.AreEqual("# Unrelated", (await skills.GetAsync("unrelated"))!.Content);
        Assert.AreEqual(0, (await costs.GetOptimizationsAsync(TaskName)).Count, "nothing changed, nothing recorded");
    }

    [TestMethod]
    public async Task Pass_AfterAChange_WaitsForNewRuns()
    {
        var (service, llm, _, _, _) = await ArrangeAsync($$"""
            { "tasks": [{ "task": "{{TaskName}}", "directive": "Skip: msg-1" }] }
            """);

        await service.RunScheduledTaskEfficiencyPassAsync(CancellationToken.None);
        await service.RunScheduledTaskEfficiencyPassAsync(CancellationToken.None);

        Assert.AreEqual(1, llm.Calls.Count, "the change resets the task's run count");
    }

    [TestMethod]
    public async Task Pass_NoCandidates_MakesNoLlmCall()
    {
        var (service, llm, _, _, _) = await ArrangeAsync("{}", runs: 2);

        await service.RunScheduledTaskEfficiencyPassAsync(CancellationToken.None);

        Assert.AreEqual(0, llm.Calls.Count);
    }

    // ── Arrangement ───────────────────────────────────────────────────────────

    private async Task<(DreamService Service, ScriptedLlm Llm, FileScheduledTaskStore Store,
        InMemorySkillResourceStore Skills, ScheduledTaskRunCosts Costs)> ArrangeAsync(
        string llmResponse, int runs = 4, bool codeOwned = false)
    {
        Directory.CreateDirectory(_dir);
        var store = new FileScheduledTaskStore(Path.Combine(_dir, "tasks.json"), NullLogger<FileScheduledTaskStore>.Instance);
        await store.SaveAsync(Weekly());

        var skills = new InMemorySkillResourceStore();
        await skills.SaveAsync(new Skill(SkillName, "Concert sweep", "Read every message in the inbox.", T0.AddDays(-60),
            CodeOwned: codeOwned));
        await skills.SaveAsync(new Skill("unrelated", "Other", "# Unrelated", T0.AddDays(-60)));
        await skills.AttachResourceAsync(SkillName, new SkillResourceInput("old.json", SkillResourceType.Wisp, "old", ReadOnlyWisp));
        await skills.AttachResourceAsync(SkillName, new SkillResourceInput("writes.json", SkillResourceType.Wisp, "sends", "send"));

        // Runs end before the real clock's now, so the optimization recorded at the clock's now
        // comes after all of them.
        var now = DateTimeOffset.UtcNow.AddDays(-7 * runs - 1);
        var ledger = 0.0;
        var costs = new ScheduledTaskRunCosts(_ => ledger, new ScheduledTaskCostOptions(),
            Path.Combine(_dir, "runs.json"), null, () => now);
        var calls = new List<ToolCallEvent>();
        var replies = new List<SavedResponse>();
        for (var i = 0; i < runs; i++)
        {
            now = now.AddDays(7);
            costs.BeginRun(TaskName, ModelTier.Balanced);
            calls.Add(new ToolCallEvent(WellKnownSessions.ScheduledTaskPrefix + TaskName, "search_messages",
                "query=concert", true, 10, now.AddMinutes(1)));
            costs.NoteActivity(TaskName, new ScheduledRunActivity(ToolCalls: 25, DuplicateFetches: 0, SkillsUsed: [SkillName]));
            ledger += 1.5;
            now = now.AddMinutes(5);
            await costs.EndRunAsync(TaskName, ScheduledRunOutcome.Replied, "Nothing new this week.");
            replies.Add(new SavedResponse($"r{i}", TaskName, "Nothing new this week.", "agent",
                "session", now, SavedResponseSources.Scheduled, AutoSaved: true));
        }

        var llm = new ScriptedLlm(llmResponse);
        var service = new DreamService(
            new EmptyMemory(),
            [skills],
            llm,
            new AgentWorkSerializer(),
            new IdleMonitor(),
            new AgentClock(new ConfigurationBuilder().Build(), Options.Create(new AgentProfileOptions()),
                NullLogger<AgentClock>.Instance),
            Options.Create(new DreamOptions { Enabled = false }),
            Options.Create(new AgentProfileOptions { BasePath = _dir }),
            NullLogger<DreamService>.Instance,
            toolCallLog: new ToolCalls(calls),
            scheduledTaskStore: store,
            scheduledTaskRunCosts: costs,
            savedWispRunner: new FakeWisps(),
            savedResponseStore: new Responses(replies));
        return (service, llm, store, skills, costs);
    }

    /// <summary>Valid when the body is JSON; read-only unless it says "send".</summary>
    private sealed class FakeWisps : ISavedWispRunner
    {
        public Task<SavedWispRun> RunAsync(string definitionJson, string? sessionId, CancellationToken ct) =>
            throw new NotSupportedException();

        public SavedWispCheck Inspect(string definitionJson) =>
            definitionJson.Contains("send", StringComparison.Ordinal)
                ? new SavedWispCheck(true, false, "step 'x' calls 'send_mail', which writes", "shape-2")
                : new SavedWispCheck(true, true, null, "shape-1");
    }

    internal sealed class ScriptedLlm(string response) : ILlmClient
    {
        public List<string> Calls { get; } = [];
        public bool IsIdle => true;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(string.Join("\n", messages.Select(m => m.Text)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetResponseAsync(messages, options, cancellationToken);
    }

    private sealed class ToolCalls(IReadOnlyList<ToolCallEvent> events) : IToolCallLog
    {
        public Task AppendAsync(ToolCallEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolCallEvent>>(events.Where(e => e.SessionId == sessionId).ToList());
        public Task<IReadOnlyList<ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            Task.FromResult(events);
    }

    private sealed class Responses(IReadOnlyList<SavedResponse> items) : ISavedResponseStore
    {
        public Task SaveAsync(SavedResponse response, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SavedResponse?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(items.FirstOrDefault(i => i.Id == id));
        public Task<IReadOnlyList<SavedResponse>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(items);
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class IdleMonitor : IUserActivityMonitor
    {
        public void RecordActivity() { }
        public bool IsUserActive(TimeSpan idleThreshold) => false;
    }

    private sealed class EmptyMemory : ILongTermMemory
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

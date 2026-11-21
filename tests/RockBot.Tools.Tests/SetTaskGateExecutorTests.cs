using RockBot.Host;
using RockBot.Tools.Scheduling;

namespace RockBot.Tools.Tests;

/// <summary>
/// <c>set_task_gate</c> (#848): puts a saved wisp in front of a task, or removes it, and leaves
/// every other field of the task alone.
/// </summary>
[TestClass]
public sealed class SetTaskGateExecutorTests
{
    private static readonly ScheduledTask Sweep = new(
        "sweep", "0 9 * * 1", "check for new mail", DateTimeOffset.UtcNow.AddDays(-30),
        LastFiredAt: DateTimeOffset.UtcNow.AddDays(-7), Directive: "skip newsletters",
        TimeZone: "home", MaxTier: ModelTier.Balanced, MaxTierReason: "cheap enough");

    private static ToolInvokeRequest Request(string json) => new()
    {
        ToolCallId = "call-1",
        ToolName = "set_task_gate",
        Arguments = json
    };

    private static ResourceSkillStore StoreWith(string skill, string resource, string body) => new(skill, resource, body);

    [TestMethod]
    public async Task Set_StoresGate_AndKeepsEverythingElse()
    {
        var scheduler = new FakeScheduler(Sweep);
        var executor = new SetTaskGateExecutor(scheduler, new FakeStore(scheduler), StoreWith("mail", "new.wisp.json", "{}"), new FakeWisps(true, true));

        var result = await executor.ExecuteAsync(
            Request("""{"name":"SWEEP","skill":"mail","resource":"new.wisp.json","mode":"changed","maxConsecutiveSkips":3}"""),
            CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        var gate = scheduler.Saved!.Gate!;
        Assert.AreEqual(new TaskGate("mail", "new.wisp.json", TaskGateMode.Changed, 3, "user", "set with set_task_gate"), gate);
        Assert.AreEqual(Sweep with { Gate = gate }, scheduler.Saved);
        StringAssert.Contains(result.Content, "mail/new.wisp.json");
        StringAssert.Contains(result.Content, "After 3 skipped runs");
        Assert.IsFalse(result.Content!.Contains("Warning"), result.Content);
    }

    [TestMethod]
    public async Task Set_DefaultsToNonEmptyAndSixSkips()
    {
        var scheduler = new FakeScheduler(Sweep);
        var executor = new SetTaskGateExecutor(scheduler, new FakeStore(scheduler));

        var result = await executor.ExecuteAsync(
            Request("""{"name":"sweep","skill":"mail","resource":"new.wisp.json"}"""), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual(TaskGateMode.NonEmpty, scheduler.Saved!.Gate!.Mode);
        Assert.AreEqual(TaskGate.DefaultMaxConsecutiveSkips, scheduler.Saved.Gate.MaxConsecutiveSkips);
    }

    [TestMethod]
    public async Task Set_MissingResource_IsError()
    {
        var scheduler = new FakeScheduler(Sweep);
        var executor = new SetTaskGateExecutor(scheduler, new FakeStore(scheduler), StoreWith("mail", "other.wisp.json", "{}"));

        var result = await executor.ExecuteAsync(
            Request("""{"name":"sweep","skill":"mail","resource":"new.wisp.json"}"""), CancellationToken.None);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "no resource 'new.wisp.json'");
        Assert.IsNull(scheduler.Saved);
    }

    [TestMethod]
    public async Task Set_InvalidWisp_IsError_AndWritingWisp_Warns()
    {
        var invalid = new FakeScheduler(Sweep);
        var badResult = await new SetTaskGateExecutor(invalid, new FakeStore(invalid), StoreWith("mail", "w.json", "x"), new FakeWisps(false, false, "no steps"))
            .ExecuteAsync(Request("""{"name":"sweep","skill":"mail","resource":"w.json"}"""), CancellationToken.None);

        var writing = new FakeScheduler(Sweep);
        var warnResult = await new SetTaskGateExecutor(writing, new FakeStore(writing), StoreWith("mail", "w.json", "x"), new FakeWisps(true, false, "step 1 sends mail"))
            .ExecuteAsync(Request("""{"name":"sweep","skill":"mail","resource":"w.json"}"""), CancellationToken.None);

        Assert.IsTrue(badResult.IsError);
        StringAssert.Contains(badResult.Content, "no steps");
        Assert.IsNull(invalid.Saved);

        Assert.IsFalse(warnResult.IsError, warnResult.Content);
        StringAssert.Contains(warnResult.Content, "not read-only (step 1 sends mail)");
        Assert.IsNotNull(writing.Saved!.Gate);
    }

    [TestMethod]
    public async Task Clear_RemovesGate_KeepsEverythingElse()
    {
        var gated = Sweep with { Gate = new TaskGate("mail", "new.wisp.json", ConsecutiveSkips: 2) };
        var scheduler = new FakeScheduler(gated);

        var result = await new SetTaskGateExecutor(scheduler, new FakeStore(scheduler))
            .ExecuteAsync(Request("""{"name":"sweep","clear":true}"""), CancellationToken.None);

        Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual(Sweep, scheduler.Saved);
        StringAssert.Contains(result.Content, "Removed the gate (mail/new.wisp.json)");
    }

    [TestMethod]
    public async Task Clear_WithoutGate_ChangesNothing()
    {
        var scheduler = new FakeScheduler(Sweep);

        var result = await new SetTaskGateExecutor(scheduler, new FakeStore(scheduler))
            .ExecuteAsync(Request("""{"name":"sweep","clear":true}"""), CancellationToken.None);

        Assert.IsFalse(result.IsError);
        Assert.IsNull(scheduler.Saved);
    }

    [TestMethod]
    public async Task UnknownTask_BadMode_MissingArgs_AreErrors()
    {
        var scheduler = new FakeScheduler(Sweep);
        var executor = new SetTaskGateExecutor(scheduler, new FakeStore(scheduler));

        var unknown = await executor.ExecuteAsync(Request("""{"name":"nope","skill":"s","resource":"r"}"""), CancellationToken.None);
        var badMode = await executor.ExecuteAsync(Request("""{"name":"sweep","skill":"s","resource":"r","mode":"sometimes"}"""), CancellationToken.None);
        var noSkill = await executor.ExecuteAsync(Request("""{"name":"sweep","resource":"r"}"""), CancellationToken.None);
        var noName = await executor.ExecuteAsync(Request("{}"), CancellationToken.None);

        Assert.IsTrue(unknown.IsError);
        StringAssert.Contains(unknown.Content, "Scheduled tasks: sweep");
        Assert.IsTrue(badMode.IsError);
        StringAssert.Contains(badMode.Content, "sometimes");
        Assert.IsTrue(noSkill.IsError);
        Assert.IsTrue(noName.IsError);
        Assert.IsNull(scheduler.Saved);
    }

    [TestMethod]
    [DataRow("nonEmpty", TaskGateMode.NonEmpty)]
    [DataRow("non-empty", TaskGateMode.NonEmpty)]
    [DataRow("CHANGED", TaskGateMode.Changed)]
    public void TryParseMode_AcceptsSpellings(string text, TaskGateMode expected)
    {
        Assert.IsTrue(SetTaskGateExecutor.TryParseMode(text, out var mode));
        Assert.AreEqual(expected, mode);
    }

    [TestMethod]
    public void TryParseMode_RejectsNumbers()
    {
        Assert.IsFalse(SetTaskGateExecutor.TryParseMode("7", out _));
    }

    [TestMethod]
    public void DescribeGate_ShowsTargetModeAndSkips()
    {
        var text = ListScheduledTasksExecutor.DescribeGate(
            new TaskGate("mail", "new.wisp.json", TaskGateMode.Changed, 6, "optimizer", ConsecutiveSkips: 2));

        Assert.AreEqual("wisp mail/new.wisp.json (mode changed; skipped 2/6 in a row, set by optimizer)", text);
    }

    [TestMethod]
    public async Task Registered_WhenTheTaskStoreIs()
    {
        var clock = new AgentClock(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentClock>.Instance);
        var scheduler = new FakeScheduler(Sweep);
        var registry = new ToolRegistry();

        await new SchedulingToolRegistrar(registry, scheduler, clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SchedulingToolRegistrar>.Instance,
            taskStore: new FakeStore(scheduler)).StartAsync(CancellationToken.None);

        var reg = registry.GetTools().Single(t => t.Name == "set_task_gate");
        Assert.AreEqual("scheduling", reg.Source);
        Assert.AreEqual("scheduling", DeferredToolFamilies.FamilyOf("set_task_gate"));
        Assert.IsTrue(ConsequentialActions.IsConsequential("set_task_gate", """{"name":"sweep"}"""),
            "a gate decides whether a task's turn runs at all");
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="Saved"/> is the task as the store last wrote it. ScheduleAsync re-arms the task's
    /// timer, which set_task_gate must not do, so calling it fails the test.
    /// </summary>
    private sealed class FakeScheduler(params ScheduledTask[] tasks) : ISchedulerService
    {
        public ScheduledTask[] Tasks { get; } = tasks;
        public ScheduledTask? Saved { get; set; }

        public Task ScheduleAsync(ScheduledTask task, CancellationToken ct = default) =>
            throw new AssertFailedException("set_task_gate re-scheduled the task (re-arming its timer)");

        public Task<bool> CancelAsync(string name, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledTask>>(tasks);

        public DateTimeOffset? GetNextOccurrence(ScheduledTask task) => DateTimeOffset.UtcNow.AddHours(1);
    }

    /// <summary>Writes only the gate, as FileScheduledTaskStore does, into the scheduler's <c>Saved</c>.</summary>
    private sealed class FakeStore(FakeScheduler scheduler) : IScheduledTaskStore
    {
        public Task<bool> UpdateGateAsync(string name, TaskGate? gate)
        {
            var task = scheduler.Tasks.FirstOrDefault(t => t.Name == name);
            if (task is null)
                return Task.FromResult(false);
            scheduler.Saved = task with { Gate = gate };
            return Task.FromResult(true);
        }

        public Task SaveAsync(ScheduledTask task) => throw new AssertFailedException("whole-task save");
        public Task<ScheduledTask?> GetAsync(string name) => Task.FromResult(scheduler.Tasks.FirstOrDefault(t => t.Name == name));
        public Task<IReadOnlyList<ScheduledTask>> ListAsync() => Task.FromResult<IReadOnlyList<ScheduledTask>>(scheduler.Tasks);
        public Task<bool> DeleteAsync(string name) => Task.FromResult(false);
        public Task UpdateLastFiredAsync(string name, DateTimeOffset firedAt) => Task.CompletedTask;
        public Task UpdateDirectiveAsync(string name, string directive) => Task.CompletedTask;
    }

    private sealed class FakeWisps(bool valid, bool readOnly, string? reason = null) : ISavedWispRunner
    {
        public Task<SavedWispRun> RunAsync(string definitionJson, string? sessionId, CancellationToken ct) =>
            throw new NotSupportedException();

        public SavedWispCheck Inspect(string definitionJson) => new(valid, readOnly, reason);
    }

    private sealed class ResourceSkillStore(string skill, string resource, string body) : ISkillStore
    {
        public Task SaveAsync(Skill s) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;

        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults,
            CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);

        public Task<string?> GetResourceAsync(string skillName, string filename) =>
            Task.FromResult(skillName == skill && filename == resource ? body : null);
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Phase 4 acceptance tests for the closed-loop apply pass. Drives
/// <see cref="DreamService.RunRepairTicketApplyAsync"/> directly with a real
/// <see cref="FileRepairTicketStore"/> plus stub appliers and verifier so the
/// full lifecycle (apply → verify → resolve/escalate, plus auto-revert) is
/// exercised end-to-end without standing up a full DreamService.
/// </summary>
[TestClass]
public class RepairTicketApplyPassTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-applypass-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task SkillBodyTicket_VerifySucceeds_TicketResolved()
    {
        var store = NewStore();
        var skillStore = new SkillBodyApplierTests.InMemorySkillStore();
        await skillStore.SaveAsync(new Skill("calendar/foo", "s", "Original body.\n", DateTimeOffset.UtcNow));

        await store.SaveAsync(NewSkillTicket("t-1", "calendar/foo"));

        var appliers = AppliersWith(new SkillBodyApplier(skillStore, NullLogger<SkillBodyApplier>.Instance));
        var verifier = StubVerifier.WithOutcomes(VerifyOutcome.PredicateSucceeded);

        await DreamService.RunRepairTicketApplyAsync(
            store, appliers, verifier, workingMemory: null,
            new RepairTicketOptions(), NullLogger.Instance, CancellationToken.None);

        var ticket = await store.GetAsync("t-1");
        Assert.AreEqual(RepairStatus.Resolved, ticket!.Status);
        Assert.AreEqual(1, ticket.Attempts.Count);
        Assert.AreEqual(VerifyOutcome.PredicateSucceeded, ticket.Attempts[0].Result.Outcome);

        // Skill must NOT have been reverted on success.
        var saved = await skillStore.GetAsync("calendar/foo");
        StringAssert.Contains(saved!.Content, "appended on Phase 4");
    }

    [TestMethod]
    public async Task SkillBodyTicket_VerifyFailsThreeTimes_Escalated_AndRevertedEachTime()
    {
        var store = NewStore();
        var skillStore = new SkillBodyApplierTests.InMemorySkillStore();
        var preBody = "Original body.\n";
        await skillStore.SaveAsync(new Skill("calendar/foo", "s", preBody, DateTimeOffset.UtcNow));

        await store.SaveAsync(NewSkillTicket("t-1", "calendar/foo"));

        var appliers = AppliersWith(new SkillBodyApplier(skillStore, NullLogger<SkillBodyApplier>.Instance));
        var verifier = StubVerifier.AlwaysFails;
        var workingMemory = new WorkingMemoryEvictApplierTests.InMemoryWorkingMemory();
        var options = new RepairTicketOptions { MaxAttempts = 3 };

        // Three apply cycles.
        for (var i = 0; i < 3; i++)
        {
            await DreamService.RunRepairTicketApplyAsync(
                store, appliers, verifier, workingMemory, options,
                NullLogger.Instance, CancellationToken.None);

            // After each cycle the skill must be back to the original body
            // because verify failed and SkillBodyApplier supports revert.
            var afterCycle = await skillStore.GetAsync("calendar/foo");
            Assert.AreEqual(preBody, afterCycle!.Content,
                $"cycle {i + 1}: skill must be reverted after failed verify");
        }

        var ticket = await store.GetAsync("t-1");
        Assert.AreEqual(RepairStatus.Escalated, ticket!.Status);
        Assert.AreEqual(3, ticket.Attempts.Count);
        Assert.IsTrue(ticket.Attempts.All(a => a.Result.Outcome == VerifyOutcome.PredicateFailed));

        var summary = await workingMemory.GetAsync(options.EscalationWmKey);
        Assert.IsNotNull(summary);
        StringAssert.Contains(summary, "t-1");
    }

    [TestMethod]
    public async Task UncertainOutcome_DoesNotCountTowardMaxAttempts()
    {
        var store = NewStore();
        var skillStore = new SkillBodyApplierTests.InMemorySkillStore();
        await skillStore.SaveAsync(new Skill("calendar/foo", "s", "body\n", DateTimeOffset.UtcNow));
        await store.SaveAsync(NewSkillTicket("t-1", "calendar/foo"));

        var appliers = AppliersWith(new SkillBodyApplier(skillStore, NullLogger<SkillBodyApplier>.Instance));
        var verifier = StubVerifier.WithOutcomes(
            VerifyOutcome.Uncertain,
            VerifyOutcome.Uncertain,
            VerifyOutcome.Uncertain);
        var options = new RepairTicketOptions { MaxAttempts = 3 };

        for (var i = 0; i < 3; i++)
        {
            await DreamService.RunRepairTicketApplyAsync(
                store, appliers, verifier, workingMemory: null, options,
                NullLogger.Instance, CancellationToken.None);
        }

        var ticket = await store.GetAsync("t-1");
        Assert.AreEqual(RepairStatus.Open, ticket!.Status);
        Assert.AreEqual(3, ticket.Attempts.Count);
    }

    [TestMethod]
    public async Task NoApplierForTarget_TicketEscalatedWithDetail()
    {
        var store = NewStore();
        await store.SaveAsync(NewSkillTicket("t-1", "calendar/foo"));

        // No SkillBody applier registered.
        var appliers = new Dictionary<RepairTarget, IRepairTargetApplier>();
        var verifier = StubVerifier.WithOutcomes(VerifyOutcome.PredicateSucceeded);
        var options = new RepairTicketOptions { MaxAttempts = 1 };

        await DreamService.RunRepairTicketApplyAsync(
            store, appliers, verifier, workingMemory: null, options,
            NullLogger.Instance, CancellationToken.None);

        var ticket = await store.GetAsync("t-1");
        Assert.AreEqual(RepairStatus.Escalated, ticket!.Status);
        StringAssert.Contains(ticket.Attempts[0].Result.Detail!, "no applier");
    }

    [TestMethod]
    public async Task DedupSameChangeHash_StaticHelper()
    {
        // ComputeNextStatus is the dedup-relevant pure function.
        var attempts = new List<RepairAttempt>
        {
            FailedAttempt(),
            FailedAttempt(),
        };
        Assert.AreEqual(RepairStatus.Open,
            DreamService.ComputeNextStatus(attempts, VerifyOutcome.PredicateFailed, maxAttempts: 3));

        attempts.Add(FailedAttempt());
        Assert.AreEqual(RepairStatus.Escalated,
            DreamService.ComputeNextStatus(attempts, VerifyOutcome.PredicateFailed, maxAttempts: 3));
    }

    [TestMethod]
    public void ComputeVerifyBackoffBudget_DoublesUpTo80Seconds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), DreamService.ComputeVerifyBackoffBudget(0));
        Assert.AreEqual(TimeSpan.FromSeconds(10), DreamService.ComputeVerifyBackoffBudget(1));
        Assert.AreEqual(TimeSpan.FromSeconds(20), DreamService.ComputeVerifyBackoffBudget(2));
        Assert.AreEqual(TimeSpan.FromSeconds(40), DreamService.ComputeVerifyBackoffBudget(3));
        Assert.AreEqual(TimeSpan.FromSeconds(80), DreamService.ComputeVerifyBackoffBudget(4));
        Assert.AreEqual(TimeSpan.FromSeconds(80), DreamService.ComputeVerifyBackoffBudget(10));
    }

    [TestMethod]
    public async Task TimeoutBackoff_PassesEscalatingBudgets_ToVerifier()
    {
        var store = NewStore();
        var skillStore = new SkillBodyApplierTests.InMemorySkillStore();
        await skillStore.SaveAsync(new Skill("calendar/foo", "s", "body\n", DateTimeOffset.UtcNow));
        await store.SaveAsync(NewSkillTicket("t-1", "calendar/foo"));

        var appliers = AppliersWith(new SkillBodyApplier(skillStore, NullLogger<SkillBodyApplier>.Instance));
        var verifier = new RecordingTimeoutVerifier();
        var options = new RepairTicketOptions { MaxAttempts = 99 };

        // Run 5 cycles. Verifier returns timeout-Uncertain each time.
        for (var i = 0; i < 5; i++)
        {
            await DreamService.RunRepairTicketApplyAsync(
                store, appliers, verifier, workingMemory: null, options,
                NullLogger.Instance, CancellationToken.None);
        }

        var expectedBudgets = new[]
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(40),
            TimeSpan.FromSeconds(80),
        };

        CollectionAssert.AreEqual(expectedBudgets, verifier.RequestedBudgets.ToArray());
    }

    [TestMethod]
    public async Task TimeoutBackoff_AfterMaxBackoffTimeouts_PromotesToFailureAndEscalates()
    {
        var store = NewStore();
        var skillStore = new SkillBodyApplierTests.InMemorySkillStore();
        await skillStore.SaveAsync(new Skill("calendar/foo", "s", "body\n", DateTimeOffset.UtcNow));
        await store.SaveAsync(NewSkillTicket("t-1", "calendar/foo"));

        var appliers = AppliersWith(new SkillBodyApplier(skillStore, NullLogger<SkillBodyApplier>.Instance));
        var verifier = new RecordingTimeoutVerifier();
        var workingMemory = new WorkingMemoryEvictApplierTests.InMemoryWorkingMemory();
        var options = new RepairTicketOptions { MaxAttempts = 3 };

        // Cycles 1..4 record TimedOut Uncertains (budget grows 5→10→20→40).
        // Cycle 5 still times out at 80s — promoted to PredicateFailed (1st failure).
        // Cycles 6..7 also promote (2nd, 3rd failure) → Escalated after the 7th cycle.
        for (var i = 0; i < 7; i++)
        {
            await DreamService.RunRepairTicketApplyAsync(
                store, appliers, verifier, workingMemory, options,
                NullLogger.Instance, CancellationToken.None);
        }

        var ticket = await store.GetAsync("t-1");
        Assert.AreEqual(RepairStatus.Escalated, ticket!.Status);

        var promotedFailures = ticket.Attempts.Count(a => a.Result.Outcome == VerifyOutcome.PredicateFailed);
        Assert.IsTrue(promotedFailures >= options.MaxAttempts,
            $"expected at least {options.MaxAttempts} PredicateFailed attempts, got {promotedFailures}");

        var summary = await workingMemory.GetAsync(options.EscalationWmKey);
        Assert.IsNotNull(summary);
    }

    [TestMethod]
    public async Task NonTimeoutUncertain_DoesNotIncrementBackoff()
    {
        var store = NewStore();
        var skillStore = new SkillBodyApplierTests.InMemorySkillStore();
        await skillStore.SaveAsync(new Skill("calendar/foo", "s", "body\n", DateTimeOffset.UtcNow));
        await store.SaveAsync(NewSkillTicket("t-1", "calendar/foo"));

        var appliers = AppliersWith(new SkillBodyApplier(skillStore, NullLogger<SkillBodyApplier>.Instance));
        // Returns Uncertain WITHOUT TimedOut flag (e.g. gateway error).
        var verifier = new GatewayErrorVerifier();
        var options = new RepairTicketOptions { MaxAttempts = 3 };

        for (var i = 0; i < 5; i++)
        {
            await DreamService.RunRepairTicketApplyAsync(
                store, appliers, verifier, workingMemory: null, options,
                NullLogger.Instance, CancellationToken.None);
        }

        // All requested budgets must be the initial 5s — non-timeout uncertains
        // should not increment the backoff counter.
        Assert.IsTrue(verifier.RequestedBudgets.All(b => b == TimeSpan.FromSeconds(5)));

        var ticket = await store.GetAsync("t-1");
        Assert.AreEqual(RepairStatus.Open, ticket!.Status);
    }

    /// <summary>Records the budget the apply pass requested on each call.</summary>
    private sealed class RecordingTimeoutVerifier : IRepairTicketVerifier
    {
        public List<TimeSpan> RequestedBudgets { get; } = [];

        public Task<VerifyResult> VerifyAsync(VerifyShape shape, CancellationToken cancellationToken = default) =>
            VerifyAsync(shape, budget: null, cancellationToken);

        public Task<VerifyResult> VerifyAsync(VerifyShape shape, TimeSpan? budget, CancellationToken cancellationToken = default)
        {
            RequestedBudgets.Add(budget ?? TimeSpan.FromSeconds(5));
            return Task.FromResult(new VerifyResult(
                VerifyOutcome.Uncertain,
                $"verify budget exceeded ({(budget ?? TimeSpan.FromSeconds(5)).TotalSeconds:F1}s)",
                TimedOut: true));
        }
    }

    /// <summary>Returns an Uncertain that is NOT a timeout (e.g. gateway down).</summary>
    private sealed class GatewayErrorVerifier : IRepairTicketVerifier
    {
        public List<TimeSpan> RequestedBudgets { get; } = [];

        public Task<VerifyResult> VerifyAsync(VerifyShape shape, CancellationToken cancellationToken = default) =>
            VerifyAsync(shape, budget: null, cancellationToken);

        public Task<VerifyResult> VerifyAsync(VerifyShape shape, TimeSpan? budget, CancellationToken cancellationToken = default)
        {
            RequestedBudgets.Add(budget ?? TimeSpan.FromSeconds(5));
            return Task.FromResult(new VerifyResult(
                VerifyOutcome.Uncertain,
                "gateway temporarily unavailable",
                TimedOut: false));
        }
    }

    [TestMethod]
    public async Task WorkingMemoryEvictTicket_IsApplied_Resolved()
    {
        var store = NewStore();
        var wm = new WorkingMemoryEvictApplierTests.InMemoryWorkingMemory();
        await wm.SetAsync("claim/capability/svr/x", "stale");
        await store.SaveAsync(NewWmEvictTicket("t-1", "claim/capability/svr/"));

        var appliers = AppliersWith(new WorkingMemoryEvictApplier(wm, NullLogger<WorkingMemoryEvictApplier>.Instance));
        var verifier = StubVerifier.WithOutcomes(VerifyOutcome.PredicateSucceeded);

        await DreamService.RunRepairTicketApplyAsync(
            store, appliers, verifier, workingMemory: null,
            new RepairTicketOptions(), NullLogger.Instance, CancellationToken.None);

        var ticket = await store.GetAsync("t-1");
        Assert.AreEqual(RepairStatus.Resolved, ticket!.Status);
        Assert.IsNull(await wm.GetAsync("claim/capability/svr/x"));
    }

    private static RepairAttempt FailedAttempt() =>
        new(DateTimeOffset.UtcNow,
            JsonDocument.Parse("{}").RootElement,
            new VerifyResult(VerifyOutcome.PredicateFailed));

    private static IReadOnlyDictionary<RepairTarget, IRepairTargetApplier> AppliersWith(IRepairTargetApplier applier) =>
        new Dictionary<RepairTarget, IRepairTargetApplier> { [applier.Target] = applier };

    private FileRepairTicketStore NewStore() =>
        new(
            Options.Create(new RepairTicketOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            NullLogger<FileRepairTicketStore>.Instance);

    private static RepairTicket NewSkillTicket(string id, string skillName) =>
        new(
            Id: id,
            PatternKey: $"svr|tool|{skillName}",
            Target: RepairTarget.SkillBody,
            Change: JsonDocument.Parse(
                "{ \"skill\": \"" + skillName + "\", \"ops\": [ { \"op\": \"append\", \"text\": \"appended on Phase 4\" } ] }"
            ).RootElement,
            Verify: new VerifyShape("svr", "tool", JsonDocument.Parse("{}").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success)),
            Attempts: [],
            Status: RepairStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static RepairTicket NewWmEvictTicket(string id, string keyPrefix) =>
        new(
            Id: id,
            PatternKey: "svr|tool|wm",
            Target: RepairTarget.WorkingMemoryEvict,
            Change: JsonDocument.Parse("{ \"keyPrefix\": \"" + keyPrefix + "\" }").RootElement,
            Verify: new VerifyShape("svr", "tool", JsonDocument.Parse("{}").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success)),
            Attempts: [],
            Status: RepairStatus.Open,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    /// <summary>Sequenceable verifier stub for driving the apply-pass loop.</summary>
    private sealed class StubVerifier : IRepairTicketVerifier
    {
        private readonly Queue<VerifyOutcome> _outcomes;

        private StubVerifier(IEnumerable<VerifyOutcome> outcomes)
        {
            _outcomes = new Queue<VerifyOutcome>(outcomes);
        }

        public static StubVerifier WithOutcomes(params VerifyOutcome[] outcomes) => new(outcomes);

        public static StubVerifier AlwaysFails =>
            new(Enumerable.Repeat(VerifyOutcome.PredicateFailed, 100));

        public Task<VerifyResult> VerifyAsync(VerifyShape shape, CancellationToken cancellationToken = default)
        {
            var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : VerifyOutcome.PredicateFailed;
            return Task.FromResult(new VerifyResult(outcome, $"stub verify → {outcome}"));
        }
    }
}

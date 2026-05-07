using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Subagent.Tests;

[TestClass]
public class SubagentResultGateTests
{
    private static SubagentResultGate CreateGate(
        int interactiveCeilingSec = 60,
        int backgroundCeilingSec = 60)
    {
        var opts = Options.Create(new SubagentOptions
        {
            InteractiveConsolidationTimeoutSeconds = interactiveCeilingSec,
            BackgroundConsolidationTimeoutSeconds = backgroundCeilingSec
        });
        return new SubagentResultGate(opts, NullLogger<SubagentResultGate>.Instance);
    }

    private static SubagentResultMessage MakeResult(
        string taskId,
        string primarySessionId = "session/session-1",
        string? batchId = "batch-1",
        bool consolidate = true) => new()
    {
        TaskId = taskId,
        SubagentSessionId = $"subagent-{taskId}",
        PrimarySessionId = primarySessionId,
        Output = $"Output from {taskId}",
        IsSuccess = true,
        Timestamp = DateTimeOffset.UtcNow,
        BatchId = batchId,
        Consolidate = consolidate
    };

    private static SubagentEntry MakeActiveEntry(
        string taskId,
        TimeSpan? runFor = null,
        string primarySessionId = "session/session-1") => new()
    {
        TaskId = taskId,
        SubagentSessionId = $"subagent-{taskId}",
        PrimarySessionId = primarySessionId,
        Description = $"Task {taskId}",
        StartedAt = DateTimeOffset.UtcNow,
        CancellationTokenSource = new CancellationTokenSource(),
        Task = Task.Delay(runFor ?? TimeSpan.FromSeconds(30)),
        BatchId = "batch-1",
        Consolidate = true
    };

    // ── Null batchId → solo synthesis ──────────────────────────────────────

    [TestMethod]
    public async Task AccumulateAsync_NullBatchId_ReturnsSoloResult()
    {
        var gate = CreateGate();
        var result = MakeResult("task-1", batchId: null);
        var manager = new FakeSubagentManager([]);

        var batch = await gate.AccumulateAsync(result, manager, CancellationToken.None);

        Assert.IsNotNull(batch);
        Assert.AreEqual(1, batch.Count);
        Assert.AreEqual("task-1", batch[0].TaskId);
    }

    // ── Consolidate=false → solo synthesis ─────────────────────────────────

    [TestMethod]
    public async Task AccumulateAsync_ConsolidateFalse_ReturnsSoloResult()
    {
        var gate = CreateGate();
        var result = MakeResult("task-1", consolidate: false);
        var manager = new FakeSubagentManager([]);

        var batch = await gate.AccumulateAsync(result, manager, CancellationToken.None);

        Assert.IsNotNull(batch);
        Assert.AreEqual(1, batch.Count);
        Assert.AreEqual("task-1", batch[0].TaskId);
    }

    // ── Single subagent with batchId, no siblings → fires immediately ──────

    [TestMethod]
    public async Task AccumulateAsync_SingleSubagent_NoSiblings_FiresImmediately()
    {
        var gate = CreateGate();
        var result = MakeResult("task-1");
        var manager = new FakeSubagentManager([]);

        var batch = await gate.AccumulateAsync(result, manager, CancellationToken.None);

        Assert.IsNotNull(batch);
        Assert.AreEqual(1, batch.Count);
        Assert.AreEqual("task-1", batch[0].TaskId);
    }

    // ── Two subagents finishing: first waits (has sibling), second fires ────

    [TestMethod]
    public async Task AccumulateAsync_TwoSubagents_LastOneFires()
    {
        var gate = CreateGate();
        var result1 = MakeResult("task-1");
        var result2 = MakeResult("task-2");

        // When task-1 arrives, task-2 is still active
        var managerWithSibling = new FakeSubagentManager([MakeActiveEntry("task-2")]);

        // First result arrives — has an active sibling, will wait
        var task1 = Task.Run(() => gate.AccumulateAsync(result1, managerWithSibling, CancellationToken.None));

        // Give task1 time to enter the wait
        await Task.Delay(200);

        // Second result arrives — no active siblings left
        var managerNoSiblings = new FakeSubagentManager([]);
        var batch2 = await gate.AccumulateAsync(result2, managerNoSiblings, CancellationToken.None);

        // The second handler should win the fire
        Assert.IsNotNull(batch2);
        Assert.AreEqual(2, batch2.Count);
        Assert.IsTrue(batch2.Any(r => r.TaskId == "task-1"));
        Assert.IsTrue(batch2.Any(r => r.TaskId == "task-2"));

        // The first handler should get null (someone else fired)
        var batch1 = await task1;
        Assert.IsNull(batch1);
    }

    // ── Sibling far apart in time: gate waits past 10 s and still consolidates ─

    [TestMethod]
    public async Task AccumulateAsync_SiblingArrivesLate_ConsolidatesIntoOneFire()
    {
        // Ceiling well above the 10 s legacy threshold so we exercise the new wait loop.
        var gate = CreateGate(interactiveCeilingSec: 30, backgroundCeilingSec: 30);
        var result1 = MakeResult("task-1");
        var result2 = MakeResult("task-2");

        var managerWithSibling = new FakeSubagentManager([MakeActiveEntry("task-2", TimeSpan.FromSeconds(20))]);

        var task1 = Task.Run(() => gate.AccumulateAsync(result1, managerWithSibling, CancellationToken.None));

        // Wait 12 s — past the old 10 s ceiling — before sibling 2 arrives.
        await Task.Delay(TimeSpan.FromSeconds(12));

        var managerNoSiblings = new FakeSubagentManager([]);
        var batch2 = await gate.AccumulateAsync(result2, managerNoSiblings, CancellationToken.None);

        Assert.IsNotNull(batch2);
        Assert.AreEqual(2, batch2.Count, "Both siblings should fire in the same batch");

        var batch1 = await task1;
        Assert.IsNull(batch1, "First sibling's invocation should yield (other fired)");
    }

    // ── Ceiling reached with stragglers → cancels and surfaces failures ────

    [TestMethod]
    public async Task AccumulateAsync_CeilingReached_CancelsStragglersAndInjectsFailures()
    {
        // 2 s ceiling so the test runs quickly.
        var gate = CreateGate(interactiveCeilingSec: 2, backgroundCeilingSec: 2);
        var result1 = MakeResult("task-1");

        var stragglerEntry = MakeActiveEntry("task-2", TimeSpan.FromMinutes(10));
        var manager = new FakeSubagentManager([stragglerEntry]);

        var batch = await gate.AccumulateAsync(result1, manager, CancellationToken.None);

        Assert.IsNotNull(batch);
        Assert.AreEqual(2, batch.Count, "Should include task-1 + a synthetic cancellation entry for task-2");
        Assert.IsTrue(batch.Any(r => r.TaskId == "task-1" && r.IsSuccess));
        var cancelled = batch.SingleOrDefault(r => r.TaskId == "task-2");
        Assert.IsNotNull(cancelled);
        Assert.IsFalse(cancelled.IsSuccess);
        Assert.IsTrue(cancelled.Error!.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(manager.CancelledTaskIds.Contains("task-2"),
            "Gate should have called CancelAsync on the straggler");
    }

    // ── Different batchIds → separate batches ──────────────────────────────

    [TestMethod]
    public async Task AccumulateAsync_DifferentBatchIds_SeparateBatches()
    {
        var gate = CreateGate();
        var result1 = MakeResult("task-1", batchId: "batch-A");
        var result2 = MakeResult("task-2", batchId: "batch-B");
        var manager = new FakeSubagentManager([]);

        var batch1 = await gate.AccumulateAsync(result1, manager, CancellationToken.None);
        var batch2 = await gate.AccumulateAsync(result2, manager, CancellationToken.None);

        Assert.IsNotNull(batch1);
        Assert.AreEqual(1, batch1.Count);
        Assert.AreEqual("task-1", batch1[0].TaskId);

        Assert.IsNotNull(batch2);
        Assert.AreEqual(1, batch2.Count);
        Assert.AreEqual("task-2", batch2[0].TaskId);
    }

    // ── Late arrival after gate fired → null (no solo synthesis) ────────────

    [TestMethod]
    public async Task AccumulateAsync_LateArrival_AfterFired_ReturnsNull()
    {
        var gate = CreateGate();
        var result1 = MakeResult("task-1");
        var manager = new FakeSubagentManager([]);

        // First result fires immediately (no siblings)
        var batch1 = await gate.AccumulateAsync(result1, manager, CancellationToken.None);
        Assert.IsNotNull(batch1);

        // Late arrival for the same batch — gate must drop it silently to avoid a
        // duplicate solo synthesis.
        var result2 = MakeResult("task-2");
        var batch2 = await gate.AccumulateAsync(result2, manager, CancellationToken.None);

        Assert.IsNull(batch2, "Late arrival to a fired batch must not trigger another synthesis");
    }

    // ── Late arrival inside ceiling+slack must return null even if Phase 2 was slow ─

    [TestMethod]
    public async Task AccumulateAsync_LateArrival_WithinCeilingPlusSlack_ReturnsNull()
    {
        // Background ceiling = 3 s. With +2 min slack the gate keeps a fired batch
        // discoverable for ~2 min 3 s. A sibling arriving 1 s after fire (well within
        // that window) must short-circuit to null, matching the production scenario
        // where Phase 2 synthesis takes longer than the legacy 30 s staleness check.
        var gate = CreateGate(backgroundCeilingSec: 3);
        var result1 = MakeResult("task-1", primarySessionId: "patrol/morning-brief");

        var batch1 = await gate.AccumulateAsync(result1, new FakeSubagentManager([]), CancellationToken.None);
        Assert.IsNotNull(batch1);

        // Simulate the time a Phase 2 synthesis would consume before the next
        // sibling result is dequeued from the broker.
        await Task.Delay(TimeSpan.FromSeconds(1));

        var result2 = MakeResult("task-2", primarySessionId: "patrol/morning-brief");
        var batch2 = await gate.AccumulateAsync(result2, new FakeSubagentManager([]), CancellationToken.None);

        Assert.IsNull(batch2,
            "Late arrival within ceiling+slack must defer to the already-fired winner, " +
            "not start a fresh batch that produces a duplicate Phase 2 synthesis.");
    }

    // ── Concurrent arrivals accumulate into a single batch ────────────────────

    [TestMethod]
    public async Task AccumulateAsync_ConcurrentArrivals_AccumulateIntoOneBatch()
    {
        // Models the post-fix runtime: with raised consumer dispatch concurrency, all
        // sibling results enter AccumulateAsync simultaneously (instead of arriving
        // serially after each Phase 2). They must coalesce into one batch with one
        // winner, not one batch per result.
        var gate = CreateGate(interactiveCeilingSec: 30);
        var primarySessionId = "session/concurrent-1";

        var entries = Enumerable.Range(1, 3)
            .Select(i => MakeActiveEntry($"task-{i}", TimeSpan.FromSeconds(20), primarySessionId))
            .ToList();

        // Start all three handlers in parallel. Each sees the others as active siblings
        // initially; the manager mutates as each task "completes" (its entry is removed).
        var manager = new FakeMutableSubagentManager(entries);
        var results = new[] { "task-1", "task-2", "task-3" }
            .Select(id => MakeResult(id, primarySessionId)).ToArray();

        var tasks = results.Select(r => Task.Run(async () =>
        {
            // Stagger entries' removal so the wait loop has something to detect.
            await Task.Delay(50);
            manager.MarkComplete(r.TaskId);
            return await gate.AccumulateAsync(r, manager, CancellationToken.None);
        })).ToArray();

        var outcomes = await Task.WhenAll(tasks);

        var winners = outcomes.Where(o => o is not null).ToList();
        Assert.AreEqual(1, winners.Count, "Exactly one handler should be the synthesis winner.");
        Assert.AreEqual(3, winners[0]!.Count, "Winner's batch must contain all three sibling results.");
        Assert.IsTrue(winners[0]!.Select(r => r.TaskId).OrderBy(x => x)
            .SequenceEqual(new[] { "task-1", "task-2", "task-3" }));
    }

    // ── Duplicate result (already fired) returns null ───────────────────────

    [TestMethod]
    public async Task AccumulateAsync_DuplicateResult_ReturnsNull()
    {
        var gate = CreateGate();
        var result1 = MakeResult("task-1");
        var manager = new FakeSubagentManager([]);

        // First call fires immediately
        var batch1 = await gate.AccumulateAsync(result1, manager, CancellationToken.None);
        Assert.IsNotNull(batch1);

        // Same taskId again — already-fired batch
        var batch2 = await gate.AccumulateAsync(result1, manager, CancellationToken.None);
        Assert.IsNull(batch2);
    }

    // ── Background ceiling chosen for non-session/ primaries ────────────────

    [TestMethod]
    public async Task AccumulateAsync_BackgroundCeiling_AppliedForPatrolPrimary()
    {
        // Interactive ceiling huge, background ceiling tiny — verify gate picked background.
        var gate = CreateGate(interactiveCeilingSec: 600, backgroundCeilingSec: 2);
        var result1 = MakeResult("task-1", primarySessionId: "patrol/heartbeat-patrol");

        var manager = new FakeSubagentManager([MakeActiveEntry("task-2", TimeSpan.FromMinutes(10), primarySessionId: "patrol/heartbeat-patrol")]);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var batch = await gate.AccumulateAsync(result1, manager, CancellationToken.None);
        sw.Stop();

        Assert.IsNotNull(batch);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Expected background ceiling (2s) + grace, got {sw.Elapsed.TotalSeconds:F1}s");
    }

    // ── Fake ────────────────────────────────────────────────────────────────

    private sealed class FakeSubagentManager(IReadOnlyList<SubagentEntry> activeEntries) : ISubagentManager
    {
        private readonly List<SubagentEntry> _active = activeEntries.ToList();
        public List<string> CancelledTaskIds { get; } = new();

        public Task<string> SpawnAsync(string description, string? context, int? timeoutMinutes,
            string primarySessionId, CancellationToken ct,
            string? batchId = null, bool consolidate = true, int? maxIterations = null) =>
            Task.FromResult("fake-task-id");

        public Task<bool> CancelAsync(string taskId)
        {
            CancelledTaskIds.Add(taskId);
            var removed = _active.RemoveAll(e => e.TaskId == taskId) > 0;
            return Task.FromResult(removed);
        }

        public IReadOnlyList<SubagentEntry> ListActive() => _active.ToList();
    }

    private sealed class FakeMutableSubagentManager(IReadOnlyList<SubagentEntry> activeEntries) : ISubagentManager
    {
        private readonly object _lock = new();
        private readonly List<SubagentEntry> _active = activeEntries.ToList();

        public Task<string> SpawnAsync(string description, string? context, int? timeoutMinutes,
            string primarySessionId, CancellationToken ct,
            string? batchId = null, bool consolidate = true, int? maxIterations = null) =>
            Task.FromResult("fake-task-id");

        public Task<bool> CancelAsync(string taskId)
        {
            lock (_lock)
            {
                var removed = _active.RemoveAll(e => e.TaskId == taskId) > 0;
                return Task.FromResult(removed);
            }
        }

        public IReadOnlyList<SubagentEntry> ListActive()
        {
            lock (_lock) return _active.ToList();
        }

        public void MarkComplete(string taskId)
        {
            lock (_lock) _active.RemoveAll(e => e.TaskId == taskId);
        }
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;

namespace RockBot.Subagent.Tests;

[TestClass]
public class SubagentResultGateTests
{
    private static SubagentResultGate CreateGate() =>
        new(NullLogger<SubagentResultGate>.Instance);

    private static SubagentResultMessage MakeResult(
        string taskId,
        string primarySessionId = "session-1",
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

    // ── Null batchId → solo synthesis ──────────────────────────────────────

    [TestMethod]
    public async Task AccumulateAsync_NullBatchId_ReturnsSoloResult()
    {
        var gate = CreateGate();
        var result = MakeResult("task-1", batchId: null);
        var manager = new FakeSubagentManager([]);

        var batch = await gate.AccumulateAsync(result, manager, 10, CancellationToken.None);

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

        var batch = await gate.AccumulateAsync(result, manager, 10, CancellationToken.None);

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

        var batch = await gate.AccumulateAsync(result, manager, 10, CancellationToken.None);

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
        var activeEntry2 = new SubagentEntry
        {
            TaskId = "task-2",
            SubagentSessionId = "subagent-task-2",
            PrimarySessionId = "session-1",
            Description = "Task 2",
            StartedAt = DateTimeOffset.UtcNow,
            CancellationTokenSource = new CancellationTokenSource(),
            Task = Task.Delay(TimeSpan.FromSeconds(30)),
            BatchId = "batch-1",
            Consolidate = true
        };
        var managerWithSibling = new FakeSubagentManager([activeEntry2]);

        // First result arrives — has an active sibling, will wait
        var task1 = Task.Run(() => gate.AccumulateAsync(result1, managerWithSibling, 5, CancellationToken.None));

        // Give task1 time to enter the wait
        await Task.Delay(200);

        // Second result arrives — no active siblings left
        var managerNoSiblings = new FakeSubagentManager([]);
        var batch2 = await gate.AccumulateAsync(result2, managerNoSiblings, 5, CancellationToken.None);

        // The second handler should win the fire
        Assert.IsNotNull(batch2);
        Assert.AreEqual(2, batch2.Count);
        Assert.IsTrue(batch2.Any(r => r.TaskId == "task-1"));
        Assert.IsTrue(batch2.Any(r => r.TaskId == "task-2"));

        // The first handler should get null (someone else fired)
        var batch1 = await task1;
        Assert.IsNull(batch1);
    }

    // ── Timeout fires with partial batch ───────────────────────────────────

    [TestMethod]
    public async Task AccumulateAsync_Timeout_FiresPartialBatch()
    {
        var gate = CreateGate();
        var result1 = MakeResult("task-1");

        // task-2 is "active" and never completes
        var activeEntry2 = new SubagentEntry
        {
            TaskId = "task-2",
            SubagentSessionId = "subagent-task-2",
            PrimarySessionId = "session-1",
            Description = "Task 2",
            StartedAt = DateTimeOffset.UtcNow,
            CancellationTokenSource = new CancellationTokenSource(),
            Task = Task.Delay(TimeSpan.FromMinutes(10)),
            BatchId = "batch-1",
            Consolidate = true
        };
        var manager = new FakeSubagentManager([activeEntry2]);

        // Short timeout — should fire after 1 second
        var batch = await gate.AccumulateAsync(result1, manager, 1, CancellationToken.None);

        Assert.IsNotNull(batch);
        Assert.AreEqual(1, batch.Count);
        Assert.AreEqual("task-1", batch[0].TaskId);
    }

    // ── Different batchIds → separate batches ──────────────────────────────

    [TestMethod]
    public async Task AccumulateAsync_DifferentBatchIds_SeparateBatches()
    {
        var gate = CreateGate();
        var result1 = MakeResult("task-1", batchId: "batch-A");
        var result2 = MakeResult("task-2", batchId: "batch-B");
        var manager = new FakeSubagentManager([]);

        var batch1 = await gate.AccumulateAsync(result1, manager, 10, CancellationToken.None);
        var batch2 = await gate.AccumulateAsync(result2, manager, 10, CancellationToken.None);

        Assert.IsNotNull(batch1);
        Assert.AreEqual(1, batch1.Count);
        Assert.AreEqual("task-1", batch1[0].TaskId);

        Assert.IsNotNull(batch2);
        Assert.AreEqual(1, batch2.Count);
        Assert.AreEqual("task-2", batch2[0].TaskId);
    }

    // ── Late arrival after gate fired → solo synthesis ──────────────────────

    [TestMethod]
    public async Task AccumulateAsync_LateArrival_AfterFired_GetsSoloSynthesis()
    {
        var gate = CreateGate();
        var result1 = MakeResult("task-1");
        var manager = new FakeSubagentManager([]);

        // First result fires immediately (no siblings)
        var batch1 = await gate.AccumulateAsync(result1, manager, 10, CancellationToken.None);
        Assert.IsNotNull(batch1);

        // Late arrival for the same batch
        var result2 = MakeResult("task-2");
        var batch2 = await gate.AccumulateAsync(result2, manager, 10, CancellationToken.None);

        // Late arrival should get solo synthesis
        Assert.IsNotNull(batch2);
        Assert.AreEqual(1, batch2.Count);
        Assert.AreEqual("task-2", batch2[0].TaskId);
    }

    // ── Duplicate result (already included) returns null ────────────────────

    [TestMethod]
    public async Task AccumulateAsync_DuplicateResult_ReturnsNull()
    {
        var gate = CreateGate();
        var result1 = MakeResult("task-1");
        var manager = new FakeSubagentManager([]);

        // First call fires immediately
        var batch1 = await gate.AccumulateAsync(result1, manager, 10, CancellationToken.None);
        Assert.IsNotNull(batch1);

        // Same taskId again — already included in the fired batch
        var batch2 = await gate.AccumulateAsync(result1, manager, 10, CancellationToken.None);
        Assert.IsNull(batch2);
    }

    // ── Fake ────────────────────────────────────────────────────────────────

    private sealed class FakeSubagentManager(IReadOnlyList<SubagentEntry> activeEntries) : ISubagentManager
    {
        public Task<string> SpawnAsync(string description, string? context, int? timeoutMinutes,
            string primarySessionId, CancellationToken ct,
            string? batchId = null, bool consolidate = true, int? maxIterations = null) =>
            Task.FromResult("fake-task-id");

        public Task<bool> CancelAsync(string taskId) =>
            Task.FromResult(false);

        public IReadOnlyList<SubagentEntry> ListActive() => activeEntries;
    }
}

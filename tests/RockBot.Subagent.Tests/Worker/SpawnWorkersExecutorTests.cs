using System.Text.Json;
using RockBot.Host;
using RockBot.Subagent.Worker;
using RockBot.Tools;

namespace RockBot.Subagent.Tests.Worker;

[TestClass]
public class SpawnWorkersExecutorTests
{
    [TestMethod]
    public async Task SpawnWorkersExecutor_ParsesDefinitionsArray_DispatchesToManager()
    {
        var manager = new FakeWorkerManager
        {
            BatchToReturn = new WorkerBatchResult
            {
                BatchId = "batch-test",
                Results =
                [
                    new WorkerResult
                    {
                        TaskId = "task-1",
                        IsSuccess = true,
                        ResultKey = "worker/task-1/result",
                    },
                    new WorkerResult
                    {
                        TaskId = "task-2",
                        IsSuccess = true,
                        ResultKey = "shared/patrol/calendar-latest",
                    },
                ],
                TotalDuration = TimeSpan.FromSeconds(2),
            },
        };

        var executor = new SpawnWorkersExecutor(manager);
        var args = JsonSerializer.Serialize(new
        {
            definitions = new object[]
            {
                new { description = "scan email", timeout_minutes = 4 },
                new { description = "scan calendar", result_key = "shared/patrol/calendar-latest" },
            },
        });

        var response = await executor.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "spawn_workers",
            Arguments = args,
            SessionId = "session-1",
        }, CancellationToken.None);

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual(2, manager.LastBatch?.Count);
        Assert.AreEqual("scan email", manager.LastBatch![0].Description);
        Assert.AreEqual(4, manager.LastBatch![0].TimeoutMinutes);
        Assert.AreEqual("shared/patrol/calendar-latest", manager.LastBatch![1].ResultKey);
        Assert.AreEqual("session-1", manager.LastPrimarySessionId);
        StringAssert.Contains(response.Content!, "batch-test");
        StringAssert.Contains(response.Content!, "task-1");
        StringAssert.Contains(response.Content!, "shared/patrol/calendar-latest");
    }

    [TestMethod]
    public async Task SpawnWorkersExecutor_MissingDefinitions_ReturnsError()
    {
        var executor = new SpawnWorkersExecutor(new FakeWorkerManager());

        var response = await executor.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "spawn_workers",
            Arguments = "{}",
        }, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "definitions");
    }

    [TestMethod]
    public async Task SpawnWorkersExecutor_EmptyArray_ReturnsError()
    {
        var executor = new SpawnWorkersExecutor(new FakeWorkerManager());
        var args = JsonSerializer.Serialize(new { definitions = Array.Empty<object>() });

        var response = await executor.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "spawn_workers",
            Arguments = args,
        }, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "at least one worker");
    }

    [TestMethod]
    public async Task SpawnWorkersExecutor_DefinitionMissingDescription_ReturnsError()
    {
        var executor = new SpawnWorkersExecutor(new FakeWorkerManager());
        var args = JsonSerializer.Serialize(new
        {
            definitions = new object[] { new { context = "no description here" } },
        });

        var response = await executor.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "spawn_workers",
            Arguments = args,
        }, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "description");
    }

    [TestMethod]
    public async Task SpawnWorkersExecutor_InvalidArgumentsJson_ReturnsError()
    {
        var executor = new SpawnWorkersExecutor(new FakeWorkerManager());

        var response = await executor.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "spawn_workers",
            Arguments = "not json",
        }, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "Invalid arguments JSON");
    }

    // ── Inlined findings (issue #493) ─────────────────────────────────────

    [TestMethod]
    public async Task SpawnWorkersExecutor_SmallResult_InlinesFindingsVerbatim()
    {
        const string findings = "8 MCP services available: todo, calendar, email, files, "
                                + "introspection, weather, search, notes.";
        var memory = new FakeWorkingMemory { Values = { ["worker/task-1/result"] = findings } };
        var executor = NewExecutor(SingleResultBatch("task-1", "worker/task-1/result"), memory);

        var response = await ExecuteAsync(executor);

        Assert.IsFalse(response.IsError, response.Content);
        StringAssert.Contains(response.Content!, findings);
        StringAssert.Contains(response.Content!, "--- task-1 (worker/task-1/result) ---");
        StringAssert.Contains(response.Content!, "this is the workers' actual output");
    }

    [TestMethod]
    public async Task SpawnWorkersExecutor_OversizedResult_ExcerptsAndOrdersAFetch()
    {
        var big = new string('x', 500) + "TAIL-MARKER";
        var memory = new FakeWorkingMemory { Values = { ["worker/task-1/result"] = big } };
        var executor = NewExecutor(
            SingleResultBatch("task-1", "worker/task-1/result"), memory, maxInlineChars: 100);

        var response = await ExecuteAsync(executor);

        StringAssert.Contains(response.Content!, "511 chars, first 100 shown");
        StringAssert.Contains(response.Content!, new string('x', 100));
        Assert.IsFalse(response.Content!.Contains("TAIL-MARKER"),
            "The tail must be withheld — that is what makes the fetch instruction necessary.");
        StringAssert.Contains(response.Content!,
            "Call get_from_working_memory('worker/task-1/result') NOW");
    }

    [TestMethod]
    public async Task SpawnWorkersExecutor_EmptyResult_SaysSoInsteadOfInventingFindings()
    {
        var memory = new FakeWorkingMemory { Values = { ["worker/task-1/result"] = "   " } };
        var executor = NewExecutor(SingleResultBatch("task-1", "worker/task-1/result"), memory);

        var response = await ExecuteAsync(executor);

        StringAssert.Contains(response.Content!, "NO CONTENT");
        StringAssert.Contains(response.Content!, "Do not report findings for this worker.");
    }

    [TestMethod]
    public async Task SpawnWorkersExecutor_MissingKey_SaysSoInsteadOfInventingFindings()
    {
        var executor = NewExecutor(
            SingleResultBatch("task-1", "worker/task-1/result"), new FakeWorkingMemory());

        var response = await ExecuteAsync(executor);

        StringAssert.Contains(response.Content!, "NO CONTENT");
    }

    [TestMethod]
    public async Task SpawnWorkersExecutor_WorkingMemoryThrows_StillReturnsMetadataReceipt()
    {
        var memory = new FakeWorkingMemory { ThrowOnGet = true };
        var executor = NewExecutor(SingleResultBatch("task-1", "worker/task-1/result"), memory);

        var response = await ExecuteAsync(executor);

        Assert.IsFalse(response.IsError, "A working-memory failure must not fail the tool call.");
        StringAssert.Contains(response.Content!, "task-1");
        StringAssert.Contains(response.Content!, "batch-test");
        StringAssert.Contains(response.Content!,
            "fetch with get_from_working_memory",
            "With nothing retrievable the receipt falls back to the key-based hint.");
    }

    [TestMethod]
    public async Task SpawnWorkersExecutor_MultipleWorkers_EachGetsItsOwnFindingsBlock()
    {
        var memory = new FakeWorkingMemory
        {
            Values =
            {
                ["worker/task-1/result"] = "email scan: 3 unread",
                ["shared/patrol/calendar-latest"] = "calendar scan: 2 meetings",
            },
        };
        var batch = new WorkerBatchResult
        {
            BatchId = "batch-test",
            Results =
            [
                new WorkerResult { TaskId = "task-1", IsSuccess = true, ResultKey = "worker/task-1/result" },
                new WorkerResult { TaskId = "task-2", IsSuccess = true, ResultKey = "shared/patrol/calendar-latest" },
            ],
            TotalDuration = TimeSpan.FromSeconds(2),
        };
        var executor = NewExecutor(batch, memory);

        var response = await ExecuteAsync(executor);

        StringAssert.Contains(response.Content!, "--- task-1 (worker/task-1/result) ---");
        StringAssert.Contains(response.Content!, "email scan: 3 unread");
        StringAssert.Contains(response.Content!, "--- task-2 (shared/patrol/calendar-latest) ---");
        StringAssert.Contains(response.Content!, "calendar scan: 2 meetings");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static WorkerBatchResult SingleResultBatch(string taskId, string resultKey) => new()
    {
        BatchId = "batch-test",
        Results = [new WorkerResult { TaskId = taskId, IsSuccess = true, ResultKey = resultKey }],
        TotalDuration = TimeSpan.FromSeconds(1),
    };

    private static SpawnWorkersExecutor NewExecutor(
        WorkerBatchResult batch, IWorkingMemory memory, int maxInlineChars = 4000) =>
        new(new FakeWorkerManager { BatchToReturn = batch }, memory, maxInlineChars);

    private static Task<ToolInvokeResponse> ExecuteAsync(SpawnWorkersExecutor executor) =>
        executor.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "spawn_workers",
            Arguments = JsonSerializer.Serialize(new
            {
                definitions = new object[] { new { description = "gather" } },
            }),
            SessionId = "session-1",
        }, CancellationToken.None);

    private sealed class FakeWorkingMemory : IWorkingMemory
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public bool ThrowOnGet { get; set; }

        public Task<string?> GetAsync(string key)
        {
            if (ThrowOnGet) throw new InvalidOperationException("working memory unavailable");
            return Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);
        }

        public Task SetAsync(string key, string value, TimeSpan? ttl = null,
            string? category = null, IReadOnlyList<string>? tags = null)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

        public Task DeleteAsync(string key) => Task.CompletedTask;

        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(
            MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }

    private sealed class FakeWorkerManager : IWorkerManager
    {
        public WorkerBatchResult? BatchToReturn { get; set; }
        public IReadOnlyList<WorkerDefinition>? LastBatch { get; private set; }
        public string? LastPrimarySessionId { get; private set; }

        public Task<WorkerBatchResult> SpawnBatchAsync(
            IReadOnlyList<WorkerDefinition> definitions,
            string primarySessionId,
            CancellationToken ct)
        {
            LastBatch = definitions;
            LastPrimarySessionId = primarySessionId;
            return Task.FromResult(BatchToReturn ?? new WorkerBatchResult
            {
                BatchId = "batch-fake",
                Results = [],
                TotalDuration = TimeSpan.Zero,
            });
        }
    }
}

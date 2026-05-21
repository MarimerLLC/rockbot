using System.Text.Json;
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

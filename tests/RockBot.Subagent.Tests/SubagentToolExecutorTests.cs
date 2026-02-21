using System.Text.Json;
using RockBot.Tools;

namespace RockBot.Subagent.Tests;

[TestClass]
public class SubagentToolExecutorTests
{
    [TestMethod]
    public async Task ListSubagentsExecutor_NoActive_ReturnsNoActiveMessage()
    {
        var manager = new FakeSubagentManager([]);
        var executor = new ListSubagentsExecutor(manager);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "list_subagents"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.AreEqual("No active subagents.", response.Content);
    }

    [TestMethod]
    public async Task ListSubagentsExecutor_WithActive_ReturnsEntries()
    {
        var entries = new List<SubagentEntry>
        {
            new()
            {
                TaskId = "abc123",
                SubagentSessionId = "subagent-abc123",
                PrimarySessionId = "session-1",
                Description = "Test task",
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
                CancellationTokenSource = new CancellationTokenSource(),
                Task = Task.CompletedTask
            }
        };
        var manager = new FakeSubagentManager(entries);
        var executor = new ListSubagentsExecutor(manager);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "list_subagents"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.IsTrue(response.Content!.Contains("abc123"));
        Assert.IsTrue(response.Content.Contains("Test task"));
    }

    [TestMethod]
    public async Task CancelSubagentExecutor_MissingTaskId_ReturnsError()
    {
        var manager = new FakeSubagentManager([]);
        var executor = new CancelSubagentExecutor(manager);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "cancel_subagent",
            Arguments = "{}"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        Assert.IsTrue(response.Content!.Contains("task_id"));
    }

    [TestMethod]
    public async Task CancelSubagentExecutor_ValidTaskId_ReturnsCancelledMessage()
    {
        var manager = new FakeSubagentManager([]) { CancelResult = true };
        var executor = new CancelSubagentExecutor(manager);
        var args = JsonSerializer.Serialize(new { task_id = "abc123" });
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "cancel_subagent",
            Arguments = args
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.IsTrue(response.Content!.Contains("cancelled"));
    }

    [TestMethod]
    public async Task CancelSubagentExecutor_UnknownTaskId_ReturnsNotFoundMessage()
    {
        var manager = new FakeSubagentManager([]) { CancelResult = false };
        var executor = new CancelSubagentExecutor(manager);
        var args = JsonSerializer.Serialize(new { task_id = "unknown" });
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "cancel_subagent",
            Arguments = args
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.IsTrue(response.Content!.Contains("No active subagent"));
    }

    [TestMethod]
    public async Task SpawnSubagentExecutor_MissingDescription_ReturnsError()
    {
        var manager = new FakeSubagentManager([]);
        var executor = new SpawnSubagentExecutor(manager);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "spawn_subagent",
            Arguments = "{}",
            SessionId = "session-1"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        Assert.IsTrue(response.Content!.Contains("description"));
    }

    [TestMethod]
    public async Task SpawnSubagentExecutor_ValidDescription_ReturnsTaskId()
    {
        var manager = new FakeSubagentManager([]) { SpawnResult = "task123" };
        var executor = new SpawnSubagentExecutor(manager);
        var args = JsonSerializer.Serialize(new { description = "Do something" });
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "spawn_subagent",
            Arguments = args,
            SessionId = "session-1"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsFalse(response.IsError);
        Assert.IsTrue(response.Content!.Contains("task123"));
    }

    [TestMethod]
    public async Task SpawnSubagentExecutor_ManagerReturnsError_PropagatesError()
    {
        var manager = new FakeSubagentManager([]) { SpawnResult = "Error: limit reached" };
        var executor = new SpawnSubagentExecutor(manager);
        var args = JsonSerializer.Serialize(new { description = "Do something" });
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "spawn_subagent",
            Arguments = args,
            SessionId = "session-1"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        Assert.IsTrue(response.Content!.StartsWith("Error:"));
    }

    [TestMethod]
    public async Task SpawnSubagentExecutor_InvalidJson_ReturnsError()
    {
        var manager = new FakeSubagentManager([]);
        var executor = new SpawnSubagentExecutor(manager);
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "spawn_subagent",
            Arguments = "not json",
            SessionId = "session-1"
        };

        var response = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(response.IsError);
        Assert.IsTrue(response.Content!.Contains("Invalid arguments JSON"));
    }

    /// <summary>
    /// Simple fake for testing executors without the full DI tree.
    /// </summary>
    private sealed class FakeSubagentManager(IReadOnlyList<SubagentEntry> activeEntries) : ISubagentManager
    {
        public string SpawnResult { get; set; } = "fake-task-id";
        public bool CancelResult { get; set; }

        public Task<string> SpawnAsync(string description, string? context, int? timeoutMinutes,
            string primarySessionId, CancellationToken ct) =>
            Task.FromResult(SpawnResult);

        public Task<bool> CancelAsync(string taskId) =>
            Task.FromResult(CancelResult);

        public IReadOnlyList<SubagentEntry> ListActive() => activeEntries;
    }
}

using ModelContextProtocol.Extensions.Tasks;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Tools.Mcp;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// The agent's half of background tasks (#658): the executor marks a call as able to take a later
/// result only for a session one can reach (never inside a wisp), and <c>mcp_cancel_task</c>
/// follows the index and travels the real executor, bus and bridge.
/// </summary>
[TestClass]
public class McpTasksAgentSideTests
{
    private const string Session = "session/test";

    private static Task<BridgeHarness> StartAsync(TaskCompletionSource<string> gate, bool tasks = true) =>
        BridgeHarness.StartAsync(
            [McpTasksEndToEndTests.ResearchTool(gate)],
            configure: e => e.Tasks = tasks ? true : null,
            configureBridge: o => o.TaskPollMinMs = 10,
            taskStore: new InMemoryMcpTaskStore { DefaultPollIntervalMs = 20 });

    private static Task<ToolInvokeResponse> CallAsync(
        McpWrapperEndToEndTests.AgentSide agent, string tool, string arguments, string session = Session) =>
        agent.Registry.GetExecutor(tool)!.ExecuteAsync(
            new ToolInvokeRequest
            {
                ToolCallId = Guid.NewGuid().ToString("N"),
                ToolName = tool,
                Arguments = arguments,
                SessionId = session,
            },
            CancellationToken.None);

    private const string Research = """{"server_name":"fixture","tool_name":"research","arguments":{"topic":"mercury"}}""";

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task McpCancelTask_IsOfferedOnlyWhileAServerRunsTasks()
    {
        var gate = new TaskCompletionSource<string>();
        await using (var withTasks = await StartAsync(gate))
        {
            var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(withTasks, McpWrapperMode.Off);
            Assert.IsNotNull(agent.Registry.GetExecutor(McpManagementExecutor.CancelTaskToolName));
        }

        await using (var without = await StartAsync(gate, tasks: false))
        {
            var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(without, McpWrapperMode.Off);
            Assert.IsNull(agent.Registry.GetExecutor(McpManagementExecutor.CancelTaskToolName),
                "every other run's tool list stays as it was");
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AConversationsCall_StartsATask_AndMcpCancelTaskStopsIt()
    {
        var gate = new TaskCompletionSource<string>();
        await using var harness = await StartAsync(gate);
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness, McpWrapperMode.Off);

        var started = await CallAsync(agent, "mcp_invoke_tool", Research);
        Assert.IsFalse(started.IsError, started.Content);
        StringAssert.Contains(started.Content, "started as a background task");
        var taskId = System.Text.RegularExpressions.Regex.Match(started.Content!, "t_[0-9a-f]{24}").Value;

        var cancelled = await CallAsync(agent, McpManagementExecutor.CancelTaskToolName, $$"""{"task_id":"{{taskId}}"}""");
        Assert.IsFalse(cancelled.IsError, cancelled.Content);
        StringAssert.Contains(cancelled.Content, "is being cancelled");

        var ended = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual("cancelled", ended.Status);

        var unknown = await CallAsync(agent, McpManagementExecutor.CancelTaskToolName, """{"task_id":"t_nope"}""");
        Assert.IsTrue(unknown.IsError);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AWispsStep_OrASessionNoLaterResultCanReach_GetsAnOrdinaryCall()
    {
        var gate = new TaskCompletionSource<string>();
        gate.SetResult("ok");
        await using var harness = await StartAsync(gate);
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness, McpWrapperMode.Off);

        using (McpTaskContext.Suppress())
            StringAssert.Contains((await CallAsync(agent, "mcp_invoke_tool", Research)).Content, "researched mercury");

        StringAssert.Contains((await CallAsync(agent, "mcp_invoke_tool", Research, session: "patrol/nightly")).Content,
            "researched mercury");
        Assert.AreEqual(0, harness.TaskResults.Count);
    }

    [TestMethod]
    public void OnlyConversationsAndSubagents_CanTakeALaterResult()
    {
        Assert.IsTrue(McpTaskContext.CanReceiveLater("session/abc"));
        Assert.IsTrue(McpTaskContext.CanReceiveLater("subagent/123"));
        Assert.IsFalse(McpTaskContext.CanReceiveLater("session/subagent-123"));
        Assert.IsFalse(McpTaskContext.CanReceiveLater("patrol/nightly"));
        Assert.IsFalse(McpTaskContext.CanReceiveLater("wisp/abc"));
        Assert.IsFalse(McpTaskContext.CanReceiveLater(null));

        using (McpTaskContext.Suppress())
            Assert.IsFalse(McpTaskContext.CanReceiveLater("session/abc"));
        Assert.IsTrue(McpTaskContext.CanReceiveLater("session/abc"), "the suppression ends with its scope");
    }
}

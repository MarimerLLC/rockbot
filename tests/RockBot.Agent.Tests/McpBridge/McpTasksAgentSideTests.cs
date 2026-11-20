extern alias fixture;

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
    public async Task TheWatchTools_AreOfferedOnlyWhileAServerAllowsWatches_AndTravelTheRealExecutor()
    {
        var store = new fixture::McpServer.ElicitationFixture.Watches.WatchableResources();
        store.Set("poll", "v1");
        var uri = fixture::McpServer.ElicitationFixture.Watches.WatchableResources.UriFor("poll");

        await using (var without = await McpWatchesEndToEndTests.StartAsync(store, policy: false))
        {
            var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(without, McpWrapperMode.Off);
            Assert.IsNull(agent.Registry.GetExecutor(McpManagementExecutor.WatchResourceToolName),
                "every other run's tool list stays as it was");
        }

        await using var harness = await McpWatchesEndToEndTests.StartAsync(store);
        var withWatches = await McpWrapperEndToEndTests.ConnectAgentAsync(harness, McpWrapperMode.Off);
        Assert.IsNotNull(withWatches.Registry.GetExecutor(McpManagementExecutor.WatchResourceToolName));
        Assert.IsNotNull(withWatches.Registry.GetExecutor(McpManagementExecutor.UnwatchResourceToolName));

        var args = $$"""{"server_name":"fixture","uri":"{{uri}}"}""";
        var watched = await CallAsync(withWatches, McpManagementExecutor.WatchResourceToolName, args);
        Assert.IsFalse(watched.IsError, watched.Content);
        StringAssert.Contains(watched.Content, "Watching");

        // A wisp's steps run inside their parent's call: never a later message.
        using (McpTaskContext.Suppress())
        {
            var inWisp = await CallAsync(withWatches, McpManagementExecutor.WatchResourceToolName, args, session: "session/other");
            Assert.IsTrue(inWisp.IsError);
            StringAssert.Contains(inWisp.Content, "Only a user conversation or a subagent");
        }

        var unwatched = await CallAsync(withWatches, McpManagementExecutor.UnwatchResourceToolName, args);
        Assert.IsFalse(unwatched.IsError, unwatched.Content);
        StringAssert.Contains(unwatched.Content, "Stopped watching");
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
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TheRunsAuthorization_IsSentWithTheCall_AndComesBackWithTheResult()
    {
        var gate = new TaskCompletionSource<string>();
        await using var harness = await StartAsync(gate);
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness, McpWrapperMode.Off);

        ToolInvokeResponse started;
        using (ActionGateContext.Set(new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.InformationOnly), new AllowAll()))
            started = await CallAsync(agent, "mcp_invoke_tool", Research);
        var taskId = System.Text.RegularExpressions.Regex.Match(started.Content!, "t_[0-9a-f]{24}").Value;

        gate.SetResult("ok");
        var result = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual("user-turn", result.RunOrigin);
        Assert.AreEqual("information-only", result.UserAskedFor);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WatchInstructions_AndRules_TakeTheUsersOwnMessage_ThroughTheRealExecutor()
    {
        // #853: what the user wants done on a change is theirs to set. A turn about a watch update, a
        // background result, a scheduled run (the dream, a patrol) or a subagent can't set or change it.
        var store = new fixture::McpServer.ElicitationFixture.Watches.WatchableResources();
        store.Set("poll", "v1");
        var uri = fixture::McpServer.ElicitationFixture.Watches.WatchableResources.UriFor("poll");
        await using var harness = await McpWatchesEndToEndTests.StartAsync(store);
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness, McpWrapperMode.Off);
        Assert.IsNotNull(agent.Registry.GetExecutor(McpManagementExecutor.ListWatchesToolName));
        Assert.IsNotNull(agent.Registry.GetExecutor(McpManagementExecutor.SetWatchRuleToolName));

        var watchArgs = $$"""{"server_name":"fixture","uri":"{{uri}}","on_change":"Finalize the best slot."}""";
        var ruleArgs = """{"server_name":"fixture","uri_pattern":"fixture://watch/*","on_change":"Propose new times."}""";
        ActionGateScope[] notTheUsers =
        [
            ActionGateScope.ForUserTurn,
            ActionGateScope.ForUserMessage.ForSubagent(),
            ActionGateScope.FromRelayed("user-turn", "instruction")!,
            ActionGateScope.ForScheduledTask,
            new ActionGateScope(RunOrigin.Background),
            ActionGateScope.ForA2AInbound,
            ActionGateScope.Unknown,
        ];
        foreach (var scope in notTheUsers)
        {
            using (ActionGateContext.Set(scope, new AllowAll()))
            {
                var watch = await CallAsync(agent, McpManagementExecutor.WatchResourceToolName, watchArgs);
                Assert.IsTrue(watch.IsError, $"{scope}: {watch.Content}");
                StringAssert.Contains(watch.Content, "only come from the user's own message");
                var rule = await CallAsync(agent, McpManagementExecutor.SetWatchRuleToolName, ruleArgs);
                Assert.IsTrue(rule.IsError, $"{scope}: {rule.Content}");
            }
        }
        Assert.AreEqual(0, harness.Bridge.ActiveWatchEntries.Count);
        Assert.AreEqual(0, (await harness.Bridge.WatchRules.ActiveAsync()).Count);

        using (ActionGateContext.Set(ActionGateScope.ForUserMessage, new AllowAll()))
        {
            var watch = await CallAsync(agent, McpManagementExecutor.WatchResourceToolName, watchArgs);
            Assert.IsFalse(watch.IsError, watch.Content);
            StringAssert.Contains(watch.Content, "> Finalize the best slot.");
            var rule = await CallAsync(agent, McpManagementExecutor.SetWatchRuleToolName, ruleArgs);
            Assert.IsFalse(rule.IsError, rule.Content);

            var tooLong = await CallAsync(agent, McpManagementExecutor.SetWatchRuleToolName,
                $$"""{"server_name":"fixture","uri_pattern":"fixture://watch/x","on_change":"{{new string('x', McpManagementExecutor.MaxWatchInstructionsChars + 1)}}"}""");
            Assert.IsTrue(tooLong.IsError);
            StringAssert.Contains(tooLong.Content, "the limit is");
        }

        // A relayed turn may still read what's set, and ask for the watch again without instructions.
        using (ActionGateContext.Set(ActionGateScope.FromRelayed("user-turn", "instruction")!, new AllowAll()))
        {
            var listing = await CallAsync(agent, McpManagementExecutor.ListWatchesToolName, "{}");
            Assert.IsFalse(listing.IsError, listing.Content);
            StringAssert.Contains(listing.Content, "> Finalize the best slot.");
            StringAssert.Contains(listing.Content, "> Propose new times.");

            var again = await CallAsync(agent, McpManagementExecutor.WatchResourceToolName, $$"""{"server_name":"fixture","uri":"{{uri}}"}""");
            Assert.IsFalse(again.IsError, again.Content);
        }
        Assert.AreEqual("Finalize the best slot.", harness.Bridge.ActiveWatchEntries.Single().OnChange);
    }

    private sealed class AllowAll : IConsequentialActionGate
    {
        public ValueTask<string?> CheckAsync(string toolName, string? arguments, ActionGateScope? scope,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
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

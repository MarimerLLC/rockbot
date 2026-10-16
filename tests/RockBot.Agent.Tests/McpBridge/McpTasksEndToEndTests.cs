using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.Tasks;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// Background tasks (#658) end to end: a real bridge against a real in-process MCP server with the
/// Tasks extension (<c>.WithTasks(InMemoryMcpTaskStore)</c>).
/// </summary>
[TestClass]
public class McpTasksEndToEndTests
{
    private const string Session = "session/test";

    private static readonly Regex TaskId = new("t_[0-9a-f]{24}", RegexOptions.Compiled);

    /// <summary>A long tool: it finishes when <paramref name="gate"/> opens, or fails when told to.</summary>
    internal static McpServerTool ResearchTool(TaskCompletionSource<string> gate, TaskCompletionSource? cancelled = null) =>
        McpServerTool.Create(
            async (string topic, CancellationToken ct) =>
            {
                try
                {
                    var how = await gate.Task.WaitAsync(ct);
                    if (how == "error")
                        throw new McpException("the archive is offline");
                    return $"researched {topic}";
                }
                catch (OperationCanceledException)
                {
                    cancelled?.TrySetResult();
                    throw;
                }
            },
            new McpServerToolCreateOptions { Name = "research", Description = "Research a topic at length." });

    /// <summary>A task tool that asks which meaning was meant, mid-run, and reports the answer.</summary>
    internal static McpServerTool AskingTool() => McpServerTool.Create(
        async (McpServer server, string topic, CancellationToken ct) =>
        {
#pragma warning disable MCP9005 // inside a task, the request goes through the task store, not the session
            var answer = await server.ElicitAsync(new ElicitRequestParams
            {
                Message = "Which Mercury do you mean?",
                RequestedSchema = new ElicitRequestParams.RequestSchema
                {
                    Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                    {
                        ["meaning"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema { Enum = ["planet", "element", "band"] },
                    },
                    Required = ["meaning"],
                },
            }, ct);
#pragma warning restore MCP9005
            var meaning = answer.Content?.TryGetValue("meaning", out var m) == true ? m.GetString() : "";
            return $"{topic}: {answer.Action}:{meaning}";
        },
        new McpServerToolCreateOptions { Name = "plan", Description = "Plan research, asking what was meant." });

    private static McpServerTool EchoTool() => McpServerTool.Create(
        (string text) => $"echo:{text}",
        new McpServerToolCreateOptions { Name = "echo" });

    private static InMemoryMcpTaskStore Store(TimeSpan? ttl = null) => new()
    {
        DefaultPollIntervalMs = 20,
        DefaultTimeToLive = ttl,
    };

    private static Task<BridgeHarness> StartAsync(
        IEnumerable<McpServerTool> tools,
        IMcpTaskStore? store = null,
        Action<McpBridgeServerConfig>? configure = null,
        Action<McpBridgeOptions>? configureBridge = null,
        IConversationMemory? conversationMemory = null,
        IWorkingMemory? workingMemory = null,
        TimeProvider? timeProvider = null,
        McpTaskRegistry? registry = null,
        bool tasks = true) =>
        BridgeHarness.StartAsync(
            tools,
            configure: e =>
            {
                e.Tasks = tasks ? true : null;
                configure?.Invoke(e);
            },
            configureBridge: o =>
            {
                o.TaskPollMinMs = 10;
                configureBridge?.Invoke(o);
            },
            conversationMemory: conversationMemory,
            workingMemory: workingMemory,
            timeProvider: timeProvider,
            taskStore: store ?? Store(),
            // echo always runs synchronously, as a tool that doesn't support tasks would.
            configureTasks: t => t.ExecutionModeSelector = r =>
                r.Params?.Name == "echo" ? McpTaskExecutionMode.Synchronous : McpTaskExecutionMode.Optional,
            taskRegistry: registry);

    private static string StartedTaskId(ToolInvokeResponse response)
    {
        Assert.IsFalse(response.IsError, response.Content);
        StringAssert.Contains(response.Content, "started as a background task");
        var match = TaskId.Match(response.Content ?? "");
        Assert.IsTrue(match.Success, response.Content);
        return match.Value;
    }

    [TestMethod]
    public async Task ACallReturnsStartedAtOnce_AndItsResultIsDeliveredLater()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workingMemory = new McpHandbackEndToEndTests.RecordingWorkingMemory();
        await using var harness = await StartAsync([ResearchTool(gate)], workingMemory: workingMemory);

        var started = await harness.InvokeAsync("research", """{"topic":"mercury"}""", sessionId: Session, canReceiveLater: true);
        var taskId = StartedTaskId(started);
        Assert.AreEqual(0, harness.TaskResults.Count, "nothing is delivered while the task runs");

        gate.SetResult("ok");
        var result = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);

        Assert.AreEqual(McpTaskResult.KindCompleted, result.Kind);
        Assert.AreEqual(Session, result.SessionId);
        Assert.AreEqual(BridgeHarness.ServerName, result.ServerName);
        Assert.AreEqual("research", result.ToolName);
        Assert.IsFalse(result.IsError);
        StringAssert.Contains(result.Content, "researched mercury");
        Assert.IsTrue(result.NeedsReply);

        // Stored in the session's working memory before it was published.
        Assert.AreEqual($"{Session}/mcp-task/{BridgeHarness.ServerName}/{taskId}/result", result.WorkingMemoryKey);
        StringAssert.Contains(await workingMemory.GetAsync(result.WorkingMemoryKey!), "researched mercury");

        // The ledger records it, delivered.
        var entry = await WaitForLedgerAsync(harness, taskId, e => e.TryGetProperty("deliveredAt", out _));
        Assert.AreEqual("completed", entry.GetProperty("status").GetString());
        Assert.AreEqual(Session, entry.GetProperty("sessionId").GetString());
    }

    [TestMethod]
    public async Task AnErrorResultIsDeliveredAsAnError()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await StartAsync([ResearchTool(gate)]);

        var taskId = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"x"}""", sessionId: Session, canReceiveLater: true));
        gate.SetResult("error");

        var result = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual(McpTaskResult.KindCompleted, result.Kind);
        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.Content, "the archive is offline");
    }

    [TestMethod]
    public async Task ATaskTheServerFails_IsReportedFailed()
    {
        // SDK 2.2.0 fails a task whose tool asks with InputRequiredException (MRTR) rather than
        // ElicitAsync: "MRTR and tasks cannot be composed via [McpServerTool] yet".
        await using var harness = await StartAsync([McpHandbackEndToEndTests.MailboxTool()]);

        var taskId = StartedTaskId(await harness.InvokeAsync("search_mail", "{}", sessionId: Session, canReceiveLater: true));

        var ended = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual(McpTaskResult.KindEnded, ended.Kind);
        Assert.AreEqual("failed", ended.Status);
        Assert.IsTrue(ended.IsError);
        StringAssert.Contains(ended.Content, "failed on the server: MRTR and tasks cannot be composed");
        Assert.IsFalse(ended.Content.Contains(".."), ended.Content);
    }

    [TestMethod]
    public async Task ACallerThatCantTakeALaterResult_GetsAnOrdinaryCall()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult("ok");
        await using var harness = await StartAsync([ResearchTool(gate)]);

        // No rb-mcp-tasks header (a wisp, a patrol), and a session no later result can reach.
        var plain = await harness.InvokeAsync("research", """{"topic":"a"}""", sessionId: Session);
        var patrol = await harness.InvokeAsync("research", """{"topic":"b"}""", sessionId: "patrol/nightly", canReceiveLater: true);

        StringAssert.Contains(plain.Content, "researched a");
        StringAssert.Contains(patrol.Content, "researched b");
        Assert.AreEqual(0, harness.TaskResults.Count);
        Assert.IsFalse(File.Exists(harness.TaskLedgerPath));
    }

    [TestMethod]
    public async Task AServerWithoutTasksOn_GetsAnOrdinaryCall()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult("ok");
        await using var harness = await StartAsync([ResearchTool(gate)], tasks: false);

        var response = await harness.InvokeAsync("research", """{"topic":"a"}""", sessionId: Session, canReceiveLater: true);

        StringAssert.Contains(response.Content, "researched a");
        Assert.AreEqual(0, harness.TaskResults.Count);
        Assert.IsFalse(harness.IndexMessages.SelectMany(m => m.Servers).Any(s => s.Tasks));
    }

    [TestMethod]
    public async Task AToolTheServerRunsSynchronously_AnswersInline()
    {
        await using var harness = await StartAsync([EchoTool()]);

        var response = await harness.InvokeAsync("echo", """{"text":"hi"}""", sessionId: Session, canReceiveLater: true);

        Assert.IsFalse(response.IsError, response.Content);
        StringAssert.Contains(response.Content, "echo:hi");
        Assert.AreEqual(0, harness.TaskResults.Count);
        Assert.IsTrue(harness.IndexMessages.SelectMany(m => m.Servers).Any(s => s.Tasks),
            "a server with tasks on says so in its summary, so mcp_cancel_task is offered");
    }

    [TestMethod]
    public async Task ATasksQuestion_IsHandedBack_AndTheAnswerLetsItFinish()
    {
        await using var harness = await StartAsync([AskingTool()],
            configure: e => e.Elicitation = new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });

        var taskId = StartedTaskId(await harness.InvokeAsync(
            "plan", """{"topic":"mercury"}""", sessionId: Session, canAnswer: true, canReceiveLater: true));

        var question = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindQuestion);
        Assert.AreEqual(taskId, question.TaskId);
        Assert.AreEqual(Session, question.SessionId);
        StringAssert.Contains(question.Content, "is paused until this question is answered");
        StringAssert.Contains(question.Content, "Which Mercury do you mean?");
        StringAssert.Contains(question.Content, question.QuestionId!);

        var answer = await harness.AnswerAsync(question.QuestionId!, """{"meaning":"planet"}""", sessionId: Session);
        Assert.IsNull(answer.Error, answer.Error);
        Assert.IsTrue(answer.TaskContinues);
        StringAssert.Contains(answer.Result!.Content, "Answer sent");

        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        StringAssert.Contains(result.Content, "mercury: accept:planet");
    }

    [TestMethod]
    public async Task ATasksQuestion_IsAnsweredInBand_WhenTheServerDoesntHandBack()
    {
        await using var harness = await StartAsync([AskingTool()],
            configure: e => e.Elicitation = new McpElicitationConfig
            {
                Defaults = new() { ["meaning"] = JsonSerializer.SerializeToElement("element") },
            });

        StartedTaskId(await harness.InvokeAsync(
            "plan", """{"topic":"mercury"}""", sessionId: Session, canAnswer: true, canReceiveLater: true));

        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        StringAssert.Contains(result.Content, "mercury: accept:element");
        Assert.IsFalse(harness.TaskResults.Any(r => r.Kind == McpTaskResult.KindQuestion));
    }

    [TestMethod]
    public async Task AnotherSession_CantAnswerATasksQuestion()
    {
        await using var harness = await StartAsync([AskingTool()],
            configure: e => e.Elicitation = new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });

        StartedTaskId(await harness.InvokeAsync(
            "plan", """{"topic":"mercury"}""", sessionId: Session, canAnswer: true, canReceiveLater: true));
        var question = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindQuestion);

        var refused = await harness.AnswerAsync(question.QuestionId!, """{"meaning":"planet"}""", sessionId: "session/other");

        StringAssert.Contains(refused.Error, "no open question");
        Assert.IsFalse(refused.TaskContinues);
    }

    [TestMethod]
    public async Task AfterARestart_ARunningTaskIsPolledAgain_AndItsResultDelivered()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await StartAsync([ResearchTool(gate)]);

        var taskId = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"mercury"}""", sessionId: Session, canReceiveLater: true));

        await harness.RestartBridgeAsync();
        gate.SetResult("ok");

        var result = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual(McpTaskResult.KindCompleted, result.Kind);
        StringAssert.Contains(result.Content, "researched mercury");
        await WaitForLedgerAsync(harness, taskId, e => e.TryGetProperty("deliveredAt", out _));
    }

    [TestMethod]
    public async Task ATaskTheServerForgets_IsReportedLost()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await StartAsync([ResearchTool(gate)], store: Store(ttl: TimeSpan.FromMilliseconds(300)));

        var taskId = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"mercury"}""", sessionId: Session, canReceiveLater: true));

        var ended = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual(McpTaskResult.KindEnded, ended.Kind);
        Assert.AreEqual("lost", ended.Status);
        Assert.IsTrue(ended.IsError);
        StringAssert.Contains(ended.Content, "server no longer has it");
    }

    [TestMethod]
    public async Task AfterARestart_ATaskTheServerLost_IsReportedLost()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await StartAsync([ResearchTool(gate)], store: Store(ttl: TimeSpan.FromSeconds(2)),
            configureBridge: o => o.TaskPollMinMs = 5_000);

        var taskId = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"mercury"}""", sessionId: Session, canReceiveLater: true));

        // The server's store forgets the task while the agent is down.
        await harness.Bridge.StopAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        await harness.RestartBridgeAsync();

        var ended = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual("lost", ended.Status);
    }

    [TestMethod]
    public async Task ARestartDuringATasksQuestion_AsksItAgain_AndAnnouncesNothing()
    {
        await using var harness = await StartAsync([AskingTool()],
            configure: e => e.Elicitation = new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });

        StartedTaskId(await harness.InvokeAsync(
            "plan", """{"topic":"mercury"}""", sessionId: Session, canAnswer: true, canReceiveLater: true));
        var first = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindQuestion);

        await harness.RestartBridgeAsync();

        var again = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindQuestion);
        Assert.AreNotEqual(first.QuestionId, again.QuestionId);
        Assert.AreEqual(0, harness.PublishedOn("user.response.test-agent").Count,
            "a task's question isn't a lost call: no interruption notice");

        var stale = await harness.AnswerAsync(first.QuestionId!, """{"meaning":"band"}""", sessionId: Session);
        Assert.IsNotNull(stale.Error);

        await harness.AnswerAsync(again.QuestionId!, """{"meaning":"band"}""", sessionId: Session);
        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        StringAssert.Contains(result.Content, "accept:band");
    }

    [TestMethod]
    public async Task PastTheSessionCap_ACallIsRefused_NamingTheRunningTasks()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await StartAsync([ResearchTool(gate)], configureBridge: o => o.MaxTasksPerSession = 1);

        var first = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"a"}""", sessionId: Session, canReceiveLater: true));
        var refused = await harness.InvokeAsync("research", """{"topic":"b"}""", sessionId: Session, canReceiveLater: true);
        var otherSession = await harness.InvokeAsync("research", """{"topic":"c"}""", sessionId: "session/other", canReceiveLater: true);

        Assert.IsTrue(refused.IsError);
        StringAssert.Contains(refused.Content, "already has 1 background MCP task");
        StringAssert.Contains(refused.Content, first);
        StartedTaskId(otherSession);
    }

    [TestMethod]
    public async Task PastTheBridgeCap_ACallIsRefused()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await StartAsync([ResearchTool(gate)], configureBridge: o => o.MaxTasks = 1);

        StartedTaskId(await harness.InvokeAsync("research", """{"topic":"a"}""", sessionId: Session, canReceiveLater: true));
        var refused = await harness.InvokeAsync("research", """{"topic":"b"}""", sessionId: "session/other", canReceiveLater: true);

        Assert.IsTrue(refused.IsError);
        StringAssert.Contains(refused.Content, "the agent is already running 1 background MCP task");
    }

    [TestMethod]
    public async Task TheAgentCanCancelItsTask_AndTheServerIsAskedToStop()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await StartAsync([ResearchTool(gate, cancelled)]);

        var taskId = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"a"}""", sessionId: Session, canReceiveLater: true));

        var otherSession = await harness.CancelTaskAsync(taskId, "session/other");
        StringAssert.Contains(otherSession.Error, "no running background task");

        var response = await harness.CancelTaskAsync(taskId, Session);
        Assert.IsNull(response.Error, response.Error);
        StringAssert.Contains(response.Message, "is being cancelled");

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var ended = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual("cancelled", ended.Status);
        Assert.IsFalse(ended.NeedsReply, "the agent asked for it; nothing to reply");

        var again = await harness.CancelTaskAsync(taskId, Session);
        StringAssert.Contains(again.Error, "already ended");
    }

    [TestMethod]
    public async Task RemovingTheServer_CancelsItsTasks()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await StartAsync([ResearchTool(gate, cancelled)], configureBridge: o => o.ConfigPollIntervalSeconds = 1);

        var taskId = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"a"}""", sessionId: Session, canReceiveLater: true));

        await File.WriteAllTextAsync(harness.ConfigPath, """{"mcpServers":{}}""");

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var ended = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual("cancelled", ended.Status);
        StringAssert.Contains(ended.Content, "was removed");
        Assert.IsTrue(ended.NeedsReply);
    }

    [TestMethod]
    public async Task ATaskPastItsTimeout_IsCancelled_AndTheSessionTold()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var harness = await StartAsync([ResearchTool(gate, cancelled)], timeProvider: clock,
            configure: e => e.TaskTimeoutMinutes = 5);

        var taskId = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"a"}""", sessionId: Session, canReceiveLater: true));

        clock.Advance(TimeSpan.FromMinutes(6));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var ended = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual("timed-out", ended.Status);
        StringAssert.Contains(ended.Content, "5-minute limit");
    }

    [TestMethod]
    public async Task ASubagentWaitsForItsTasks()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new McpTaskRegistry();
        var workingMemory = new McpHandbackEndToEndTests.RecordingWorkingMemory();
        await using var harness = await StartAsync([ResearchTool(gate)], registry: registry, workingMemory: workingMemory);
        const string subagent = "subagent/abc123";

        var taskId = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"a"}""", sessionId: subagent, canReceiveLater: true));

        var awaiter = new McpTaskSessionAwaiter(registry);
        var waiting = awaiter.WaitForSessionAsync(subagent, CancellationToken.None);
        await Task.Delay(100);
        Assert.IsFalse(waiting.IsCompleted, "the subagent waits while its task runs");

        gate.SetResult("ok");
        Assert.AreEqual(1, await waiting.WaitAsync(TimeSpan.FromSeconds(10)));

        // By the time the wait ends, the result is among the subagent's outputs.
        StringAssert.Contains(workingMemory.Store[$"{subagent}/mcp-task/{BridgeHarness.ServerName}/{taskId}/result"], "researched a");
    }

    [TestMethod]
    public async Task TheCallersAuthorization_TravelsWithTheTask_AcrossARestart()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await StartAsync([ResearchTool(gate)]);

        var taskId = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"a"}""", sessionId: Session,
            canReceiveLater: true, extraHeaders: new Dictionary<string, string>
            {
                [McpHeaders.RunOrigin] = "user-turn",
                [McpHeaders.UserAskedFor] = "information-only",
            }));
        await harness.RestartBridgeAsync();
        gate.SetResult("ok");

        var result = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual("user-turn", result.RunOrigin);
        Assert.AreEqual("information-only", result.UserAskedFor);
    }

    [TestMethod]
    public async Task AQuestionThatCantBeDelivered_IsDeclined_RatherThanLeftWaiting()
    {
        await using var harness = await StartAsync([AskingTool()],
            configure: e => e.Elicitation = new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });
        harness.PublishGate = e =>
            e.MessageType == typeof(McpTaskResult).FullName && e.GetPayload<McpTaskResult>()!.Kind == McpTaskResult.KindQuestion
                ? throw new InvalidOperationException("broker unavailable")
                : Task.CompletedTask;

        StartedTaskId(await harness.InvokeAsync(
            "plan", """{"topic":"mercury"}""", sessionId: Session, canAnswer: true, canReceiveLater: true));

        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        StringAssert.Contains(result.Content, "mercury: decline:");
    }

    [TestMethod]
    public async Task AFailedDelivery_IsRetried_AndASubagentWaitsForIt()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new McpTaskRegistry();
        await using var harness = await StartAsync([ResearchTool(gate)], registry: registry);
        var failures = 0;
        harness.PublishGate = e =>
            e.MessageType == typeof(McpTaskResult).FullName && Interlocked.Increment(ref failures) <= 2
                ? throw new InvalidOperationException("broker unavailable")
                : Task.CompletedTask;
        const string subagent = "subagent/retry";

        var taskId = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"a"}""", sessionId: subagent, canReceiveLater: true));
        var waiting = registry.WaitForSessionAsync(subagent, CancellationToken.None);
        gate.SetResult("ok");

        var result = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        StringAssert.Contains(result.Content, "researched a");
        Assert.IsTrue(failures >= 3, "delivered on a retry");
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForLedgerAsync(harness, taskId, e => e.TryGetProperty("deliveredAt", out _));
    }

    [TestMethod]
    public async Task RemovingARenamedServer_CancelsTheTasksStartedUnderItsOldName()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await StartAsync([ResearchTool(gate, cancelled)], configureBridge: o => o.ConfigPollIntervalSeconds = 1);

        var taskId = StartedTaskId(await harness.InvokeAsync("research", """{"topic":"a"}""", sessionId: Session, canReceiveLater: true));

        // Renamed while the agent is down; the entry keeps its stable id.
        await harness.Bridge.StopAsync(CancellationToken.None);
        var config = harness.ReadPersistedConfig();
        var entry = config.McpServers[BridgeHarness.ServerName];
        config.McpServers.Remove(BridgeHarness.ServerName);
        config.McpServers["renamed"] = entry;
        await File.WriteAllTextAsync(harness.ConfigPath, JsonSerializer.Serialize(config,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        await harness.RestartBridgeAsync();

        await File.WriteAllTextAsync(harness.ConfigPath, """{"mcpServers":{}}""");

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var ended = await harness.WaitForTaskResultAsync(r => r.TaskId == taskId);
        Assert.AreEqual("cancelled", ended.Status);
    }

    [TestMethod]
    public async Task ASurfaceRefresh_KeepsTheServersTasksAndHandbackFlags()
    {
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await StartAsync([ResearchTool(gate)],
            configure: e => e.Elicitation = new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });

        harness.AddServerTool(EchoTool());
        Assert.IsTrue(await harness.Bridge.RefreshSurfaceAsync(BridgeHarness.ServerName, CancellationToken.None));

        var summary = harness.IndexMessages.Last().Servers.Single();
        Assert.IsTrue(summary.Tasks, "mcp_cancel_task must stay registered");
        Assert.IsTrue(summary.Handback, "mcp_answer must stay registered");
    }

    /// <summary>Waits for the task's ledger entry to satisfy <paramref name="match"/>; returns it as JSON.</summary>
    internal static async Task<JsonElement> WaitForLedgerAsync(BridgeHarness harness, string taskId, Func<JsonElement, bool> match)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            if (File.Exists(harness.TaskLedgerPath))
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(harness.TaskLedgerPath));
                var entry = doc.RootElement.GetProperty("entries").EnumerateArray()
                    .FirstOrDefault(e => e.GetProperty("taskId").GetString() == taskId);
                if (entry.ValueKind == JsonValueKind.Object && match(entry))
                    return entry.Clone();
            }

            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Task {taskId}'s ledger entry never matched.");
            await Task.Delay(25);
        }
    }
}

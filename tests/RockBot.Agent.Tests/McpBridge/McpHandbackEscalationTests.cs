using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.Handback;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Elicitation;
using RockBot.UserProxy;
using static RockBot.Agent.Tests.McpBridge.McpHandbackEndToEndTests;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// A subagent escalating a handed-back question to the user through its primary agent (#659,
/// <c>design/mcp-elicitation-handback.md</c>, "Subagents"): a real bridge and MRTR server, the
/// question re-bound to the primary session, the decision rule there, and the resumed call's
/// outcome going to the subagent while it waits, or to the primary once it doesn't.
/// </summary>
[TestClass]
public class McpHandbackEscalationTests
{
    private const string Subagent = "subagent/task-1";
    private const string Primary = "session/test";
    private const string PrimaryConversation = "test";

    /// <summary>MRTR tool whose question is a yes/no decision. With a gate, it waits on it after the answer.</summary>
    private static McpServerTool ConfirmTool(TaskCompletionSource? gate = null) => McpServerTool.Create(
        async (RequestContext<CallToolRequestParams> context) =>
        {
            if (context.Params?.RequestState is not null)
            {
                if (gate is not null)
                    await gate.Task;
                var answer = context.Params.InputResponses!["confirm"].Deserialize(InputResponse.ElicitResultJsonTypeInfo)!;
                return answer.Action + ":" + (answer.Content?.TryGetValue("confirm", out var c) == true ? c.GetRawText() : "");
            }

            throw new InputRequiredException(
                new Dictionary<string, InputRequest>
                {
                    ["confirm"] = InputRequest.ForElicitation(new ElicitRequestParams
                    {
                        Message = "Delete 12 rows?",
                        RequestedSchema = new ElicitRequestParams.RequestSchema
                        {
                            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                            {
                                ["confirm"] = new ElicitRequestParams.BooleanSchema(),
                            },
                            Required = ["confirm"],
                        },
                    }),
                },
                "1");
        },
        new McpServerToolCreateOptions { Name = "delete_rows" });

    /// <summary>
    /// MRTR tool that asks which mailbox, then — once <paramref name="gate"/> opens — asks a
    /// second question, so a test can change things between the user's answer and the next question.
    /// </summary>
    private static McpServerTool GatedTwoQuestionTool(TaskCompletionSource gate) => McpServerTool.Create(
        async (RequestContext<CallToolRequestParams> context) =>
        {
            var round = context.Params?.RequestState is { } state ? int.Parse(state) : 0;
            if (round >= 2)
                return "done";
            if (round == 1)
                await gate.Task;

            throw new InputRequiredException(
                new Dictionary<string, InputRequest>
                {
                    [$"q{round}"] = InputRequest.ForElicitation(new ElicitRequestParams
                    {
                        Message = round == 0 ? "Which mailbox?" : "Which folder?",
                        RequestedSchema = new ElicitRequestParams.RequestSchema
                        {
                            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                            {
                                [$"q{round}"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema { Enum = ["a", "b"] },
                            },
                            Required = [$"q{round}"],
                        },
                    }),
                },
                (round + 1).ToString());
        },
        new McpServerToolCreateOptions { Name = "two_questions" });

    private static Task<BridgeHarness> StartAsync(
        FakeSubagentSessions sessions,
        IConversationMemory conversation,
        IEnumerable<McpServerTool>? tools = null,
        IWorkingMemory? workingMemory = null,
        TimeProvider? timeProvider = null,
        Action<McpBridgeOptions>? configureBridge = null) =>
        BridgeHarness.StartAsync(
            tools ?? [ConfirmTool(), MailboxTool(rounds: 2)],
            configure: e => e.Elicitation = new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback },
            configureBridge: o =>
            {
                // Retries of a failed fold-back come quickly in tests.
                o.TaskPollMinMs = 50;
                o.TaskPollMaxMs = 200;
                configureBridge?.Invoke(o);
            },
            conversationMemory: conversation,
            workingMemory: workingMemory,
            timeProvider: timeProvider,
            subagentSessions: sessions);

    /// <summary>
    /// Escalates <c>two_questions</c>' first question and has the primary answer it while the
    /// subagent waits; then the subagent stops waiting. The server's second question is held by the
    /// gate, so the test decides what happens before it arrives.
    /// </summary>
    private static async Task<string> AnswerFirstThenDetachAsync(BridgeHarness harness)
    {
        var handedBack = await harness.InvokeAsync("two_questions", "{}", sessionId: Subagent, canAnswer: true);
        var id = IdIn(handedBack.Content);
        await harness.EscalateAsync(id, Subagent);
        await WaitForEscalatedQuestionAsync(harness, id);

        var first = await harness.AnswerAsync(id, """{"q0":"a"}""", sessionId: Primary);
        Assert.IsTrue(first.TaskContinues, "the subagent still waited when the primary answered");

        await harness.DeliverAsync(McpManagementExecutor.ManageTopic, new McpDetachEscalationRequest { QuestionId = id, SessionId = Subagent });
        await Task.Delay(100);
        return id;
    }

    private static async Task<string> HandBackToSubagentAsync(BridgeHarness harness, string tool = "delete_rows")
    {
        var handedBack = await harness.InvokeAsync(tool, "{}", sessionId: Subagent, canAnswer: true);
        StringAssert.Contains(handedBack.Content, "escalate: true", "a subagent is told how to pass the question up");
        return IdIn(handedBack.Content);
    }

    private static Task<McpTaskResult> WaitForEscalatedQuestionAsync(BridgeHarness harness, string id) =>
        harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindEscalatedQuestion && r.QuestionId == id);

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WhileTheSubagentWaits_TheUsersAnswerResumesTheCall_AndTheResultGoesToTheSubagent()
    {
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        var conversation = new RecordingConversationMemory();
        var working = new RecordingWorkingMemory();
        await using var harness = await StartAsync(sessions, conversation, workingMemory: working);
        var id = await HandBackToSubagentAsync(harness);

        var subagentReply = await harness.EscalateAsync(id, Subagent);

        // The primary is told, as a turn in the user's conversation, and it's in working memory.
        var notice = await WaitForEscalatedQuestionAsync(harness, id);
        Assert.AreEqual(Primary, notice.SessionId);
        Assert.AreEqual("task-1", notice.TaskId);
        StringAssert.Contains(notice.Content, "Subagent task-1 needs the user's answer");
        StringAssert.Contains(notice.Content, "Delete 12 rows?");
        StringAssert.Contains(notice.Content, id);
        Assert.IsTrue(working.Store.ContainsKey($"{Primary}/mcp-escalated/{id}"));

        // The binding moved to the primary session, in the ledger too.
        var entry = ReadLedger(harness).Single();
        Assert.AreEqual(Primary, entry.SessionId);
        Assert.AreEqual(Subagent, entry.EscalatedFrom);

        // A decision needs the user to have spoken in the primary session since the escalation.
        var tooSoon = await harness.AnswerAsync(id, """{"confirm":true}""", sessionId: Primary);
        StringAssert.Contains(tooSoon.Error, "decision");

        await conversation.AddTurnAsync(PrimaryConversation, new ConversationTurn("user", "yes, delete them", DateTimeOffset.UtcNow));
        var confirmed = await harness.AnswerAsync(id, """{"confirm":true}""", sessionId: Primary);

        // The primary only hears the answer was sent; the result goes to the waiting subagent.
        Assert.IsNull(confirmed.Error, confirmed.Error);
        Assert.IsTrue(confirmed.TaskContinues);
        StringAssert.Contains(confirmed.Result!.Content, "goes back to subagent task-1");

        var resumed = (await harness.WaitForAsync(subagentReply, TimeSpan.FromSeconds(15))).GetPayload<McpAnswerQuestionResponse>()!;
        Assert.IsNull(resumed.Error, resumed.Error);
        StringAssert.StartsWith(resumed.Result!.Content, "accept:true");
        StringAssert.Contains(resumed.Result.Content, "answered by the primary agent with the user's reply",
            "the note must not read as the subagent's own answer");
        Assert.IsFalse(harness.TaskResults.Any(r => r.Kind == McpTaskResult.KindEscalatedResult),
            "the subagent took the result; nothing folds back");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WhenTheSubagentHasFinished_TheQuestionStaysOpen_AndThePrimaryGetsTheResult()
    {
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        var conversation = new RecordingConversationMemory();
        await using var harness = await StartAsync(sessions, conversation);
        var id = await HandBackToSubagentAsync(harness);

        var subagentReply = await harness.EscalateAsync(id, Subagent);
        await WaitForEscalatedQuestionAsync(harness, id);

        // The run ends: the session-end release must not cancel what it escalated.
        await harness.DeliverAsync(McpManagementExecutor.ManageTopic, new McpReleaseSessionQuestionsRequest { SessionId = Subagent });
        sessions.Finish(Subagent);

        await conversation.AddTurnAsync(PrimaryConversation, new ConversationTurn("user", "go ahead", DateTimeOffset.UtcNow));
        var confirmed = await harness.AnswerAsync(id, """{"confirm":true}""", sessionId: Primary);

        Assert.IsNull(confirmed.Error, confirmed.Error);
        Assert.IsFalse(confirmed.TaskContinues);
        StringAssert.StartsWith(confirmed.Result!.Content, "accept:true");
        Assert.AreEqual(0, harness.PublishedOn(subagentReply).Count, "nobody waits on the subagent's reply any more");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WhenTheSubagentStopsWaitingAfterTheAnswer_TheResultFoldsBackToThePrimary()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        var conversation = new RecordingConversationMemory();
        var working = new RecordingWorkingMemory();
        await using var harness = await StartAsync(sessions, conversation, tools: [ConfirmTool(gate)], workingMemory: working);
        var id = await HandBackToSubagentAsync(harness);

        var subagentReply = await harness.EscalateAsync(id, Subagent);
        await WaitForEscalatedQuestionAsync(harness, id);

        await conversation.AddTurnAsync(PrimaryConversation, new ConversationTurn("user", "yes", DateTimeOffset.UtcNow));
        var confirmed = await harness.AnswerAsync(id, """{"confirm":true}""", sessionId: Primary);
        Assert.IsTrue(confirmed.TaskContinues, "the subagent was still waiting when the user answered");

        // The subagent's wait times out while the server is still working.
        await harness.DeliverAsync(McpManagementExecutor.ManageTopic, new McpDetachEscalationRequest { QuestionId = id, SessionId = Subagent });
        await Task.Delay(100);
        gate.SetResult();

        var folded = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindEscalatedResult);
        Assert.AreEqual(Primary, folded.SessionId);
        Assert.AreEqual("task-1", folded.TaskId);
        Assert.AreEqual(id, folded.QuestionId);
        StringAssert.StartsWith(folded.Content, "accept:true");
        Assert.IsTrue(working.Store.ContainsKey($"{Primary}/mcp-escalated/{id}/result"));
        Assert.AreEqual(0, harness.PublishedOn(subagentReply).Count);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AFoldedResultThatFailsToDeliver_IsRetried_NotDropped()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        var conversation = new RecordingConversationMemory();
        await using var harness = await StartAsync(sessions, conversation, tools: [ConfirmTool(gate)]);
        var id = await HandBackToSubagentAsync(harness);

        await harness.EscalateAsync(id, Subagent);
        await WaitForEscalatedQuestionAsync(harness, id);
        await conversation.AddTurnAsync(PrimaryConversation, new ConversationTurn("user", "yes", DateTimeOffset.UtcNow));
        await harness.AnswerAsync(id, """{"confirm":true}""", sessionId: Primary);
        await harness.DeliverAsync(McpManagementExecutor.ManageTopic, new McpDetachEscalationRequest { QuestionId = id, SessionId = Subagent });
        await Task.Delay(100);

        // The broker refuses the first two attempts to deliver the folded result.
        var failures = 0;
        harness.PublishGate = e =>
            e.GetPayload<McpTaskResult>() is { Kind: McpTaskResult.KindEscalatedResult } && Interlocked.Increment(ref failures) <= 2
                ? throw new InvalidOperationException("broker unavailable")
                : Task.CompletedTask;
        gate.SetResult();

        var folded = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindEscalatedResult);
        StringAssert.StartsWith(folded.Content, "accept:true");
        Assert.AreEqual(3, failures, "delivered on the third attempt");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AFollowUpQuestionOverThePrimarysCap_EndsTheCall_AndThePrimaryIsTold()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        await using var harness = await StartAsync(sessions, new RecordingConversationMemory(),
            tools: [GatedTwoQuestionTool(gate), ConfirmTool()],
            configureBridge: o => o.MaxPendingQuestionsPerSession = 1);
        await AnswerFirstThenDetachAsync(harness);

        // The primary's own call takes its one open question, so the follow-up has no room.
        IdIn((await harness.InvokeAsync("delete_rows", "{}", sessionId: Primary, canAnswer: true)).Content);
        gate.SetResult();

        var ended = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindEscalatedResult);
        Assert.AreEqual(Primary, ended.SessionId);
        Assert.IsTrue(ended.IsError);
        StringAssert.Contains(ended.Content, "could not be passed to the user");
        StringAssert.Contains(ended.Content, "the call was cancelled");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AFollowUpQuestionThatCantBeDelivered_EndsTheCall_AndThePrimaryIsTold()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        await using var harness = await StartAsync(sessions, new RecordingConversationMemory(),
            tools: [GatedTwoQuestionTool(gate)]);
        var first = await AnswerFirstThenDetachAsync(harness);

        // The notice of the server's second question can't be delivered.
        harness.PublishGate = e =>
            e.GetPayload<McpTaskResult>() is { Kind: McpTaskResult.KindEscalatedQuestion } notice && notice.QuestionId != first
                ? throw new InvalidOperationException("broker unavailable")
                : Task.CompletedTask;
        gate.SetResult();

        var ended = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindEscalatedResult);
        Assert.IsTrue(ended.IsError);
        StringAssert.Contains(ended.Content, "could not be delivered to the primary agent");
        Assert.AreEqual(PendingQuestionStatus.Cancelled,
            ReadLedger(harness).Single(e => e.QuestionId != first).Status, "nothing is left parked until it expires");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task OnlyTheSessionTheQuestionIsBoundToMayAnswerOrDetachIt()
    {
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        var conversation = new RecordingConversationMemory();
        await using var harness = await StartAsync(sessions, conversation);
        var id = await HandBackToSubagentAsync(harness);

        // A user session can't escalate: it asks the user itself.
        var userCall = IdIn((await harness.InvokeAsync("delete_rows", "{}", sessionId: Primary, canAnswer: true)).Content);
        var notEscalated = (await harness.WaitForAsync(await harness.EscalateAsync(userCall, Primary), TimeSpan.FromSeconds(10)))
            .GetPayload<McpAnswerQuestionResponse>()!;
        StringAssert.Contains(notEscalated.Error, "You can ask the user yourself");

        await harness.EscalateAsync(id, Subagent);
        await WaitForEscalatedQuestionAsync(harness, id);

        // The subagent gave the question away: it can't answer it, nor escalate it again.
        var bySubagent = await harness.AnswerAsync(id, answersJson: null, decline: true, sessionId: Subagent);
        StringAssert.Contains(bySubagent.Error, "no open question");
        var again = (await harness.WaitForAsync(await harness.EscalateAsync(id, Subagent), TimeSpan.FromSeconds(10)))
            .GetPayload<McpAnswerQuestionResponse>()!;
        StringAssert.Contains(again.Error, "no open question");

        // Nor can another conversation; and another subagent can't detach it.
        await conversation.AddTurnAsync("other", new ConversationTurn("user", "yes", DateTimeOffset.UtcNow));
        var byOther = await harness.AnswerAsync(id, """{"confirm":true}""", sessionId: "session/other");
        StringAssert.Contains(byOther.Error, "no open question");
        await harness.DeliverAsync(McpManagementExecutor.ManageTopic, new McpDetachEscalationRequest { QuestionId = id, SessionId = "subagent/task-2" });

        await conversation.AddTurnAsync(PrimaryConversation, new ConversationTurn("user", "yes", DateTimeOffset.UtcNow));
        var confirmed = await harness.AnswerAsync(id, """{"confirm":true}""", sessionId: Primary);
        Assert.IsTrue(confirmed.TaskContinues, "the wrong subagent's detach was ignored");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WithNoUserConversationAbove_TheSubagentIsToldToDecline()
    {
        var sessions = new FakeSubagentSessions().Running(Subagent, "subagent/parent");
        await using var harness = await StartAsync(sessions, new RecordingConversationMemory());
        var id = await HandBackToSubagentAsync(harness);

        var refused = (await harness.WaitForAsync(await harness.EscalateAsync(id, Subagent), TimeSpan.FromSeconds(10)))
            .GetPayload<McpAnswerQuestionResponse>()!;

        StringAssert.Contains(refused.Error, "no user conversation");
        var declined = await harness.AnswerAsync(id, answersJson: null, decline: true, sessionId: Subagent);
        Assert.IsNull(declined.Error, "the question is still the subagent's to decline");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WhenTheServerAsksAgainAfterTheSubagentHasFinished_TheNextQuestionGoesToThePrimary()
    {
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        var conversation = new RecordingConversationMemory();
        await using var harness = await StartAsync(sessions, conversation);
        var id = await HandBackToSubagentAsync(harness, "search_mail");

        await harness.EscalateAsync(id, Subagent);
        await WaitForEscalatedQuestionAsync(harness, id);
        await harness.DeliverAsync(McpManagementExecutor.ManageTopic, new McpReleaseSessionQuestionsRequest { SessionId = Subagent });
        sessions.Finish(Subagent);

        // The primary answers; the server asks again, and that question is the primary's too.
        var first = await harness.AnswerAsync(id, """{"mailbox":"work"}""", sessionId: Primary);
        var next = IdIn(first.Result!.Content);
        Assert.AreNotEqual(id, next);
        StringAssert.Contains(first.Result.Content, "ask the user first", "it's a user conversation's question now");

        var second = await harness.AnswerAsync(next, """{"mailbox":"personal"}""", sessionId: Primary);
        Assert.IsNull(second.Error, second.Error);
        StringAssert.StartsWith(second.Result!.Content, "round 2 accept:personal");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WhenAnEscalatedQuestionExpires_TheWaitingSubagentIsTold()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        await using var harness = await StartAsync(sessions, new RecordingConversationMemory(), timeProvider: clock);
        var id = await HandBackToSubagentAsync(harness);

        var subagentReply = await harness.EscalateAsync(id, Subagent);
        await WaitForEscalatedQuestionAsync(harness, id);

        clock.Advance(TimeSpan.FromMinutes(31));

        var closed = (await harness.WaitForAsync(subagentReply, TimeSpan.FromSeconds(10))).GetPayload<McpAnswerQuestionResponse>()!;
        StringAssert.Contains(closed.Error, "The question you passed to the user is closed");
        Assert.IsTrue(closed.QuestionClosed);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARestartAnnouncesAnEscalatedQuestionToThePrimary()
    {
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        var conversation = new RecordingConversationMemory();
        await using var harness = await StartAsync(sessions, conversation, workingMemory: new RecordingWorkingMemory());
        var id = await HandBackToSubagentAsync(harness);

        await harness.EscalateAsync(id, Subagent);
        await WaitForEscalatedQuestionAsync(harness, id);
        await harness.RestartBridgeAsync();

        var notice = (await harness.WaitForAsync("user.response.test-agent", TimeSpan.FromSeconds(10))).GetPayload<AgentReply>()!;
        Assert.AreEqual(PrimaryConversation, notice.SessionId);
        StringAssert.Contains(notice.Content, "Subagent task-1 had called `delete_rows`");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TheExecutor_RejectsEscalateWithAnAnswer()
    {
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        await using var harness = await StartAsync(sessions, new RecordingConversationMemory());
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness, McpWrapperMode.Off);

        var mixed = await CallAsync(agent, McpHandbackContext.AnswerToolName,
            """{"question_id":"q_1","escalate":true,"answers":{"confirm":true}}""", Subagent);

        Assert.IsTrue(mixed.IsError);
        StringAssert.Contains(mixed.Content, "don't combine");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WhenTheSubagentsWaitTimesOut_ItIsToldToFinish_AndTheResultGoesToThePrimary()
    {
        var sessions = new FakeSubagentSessions().Running(Subagent, Primary);
        var conversation = new RecordingConversationMemory();
        await using var harness = await StartAsync(sessions, conversation);
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness, McpWrapperMode.Off,
            responseTimeout: TimeSpan.FromMilliseconds(500));
        var id = await HandBackToSubagentAsync(harness);

        // The subagent's mcp_answer(escalate) through the real executor; the user doesn't answer in time.
        var waited = await CallAsync(agent, McpHandbackContext.AnswerToolName,
            $$$"""{"question_id":"{{{id}}}","escalate":true}""", Subagent);
        Assert.IsFalse(waited.IsError, waited.Content);
        StringAssert.Contains(waited.Content, "The user hasn't answered yet");
        await WaitForEscalatedQuestionAsync(harness, id);

        // The executor told the bridge it stopped waiting, so the primary's answer gets the result.
        await conversation.AddTurnAsync(PrimaryConversation, new ConversationTurn("user", "yes", DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => harness.BridgeLog.Any(l => l.Message.Contains("stopped waiting for escalated MCP question")));
        var confirmed = await harness.AnswerAsync(id, """{"confirm":true}""", sessionId: Primary);

        Assert.IsNull(confirmed.Error, confirmed.Error);
        Assert.IsFalse(confirmed.TaskContinues, "the subagent had stopped waiting");
        StringAssert.StartsWith(confirmed.Result!.Content, "accept:true");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition not met within 10 s.");
            await Task.Delay(25);
        }
    }

    private static Task<ToolInvokeResponse> CallAsync(
        McpWrapperEndToEndTests.AgentSide agent, string tool, string arguments, string sessionId) =>
        agent.Registry.GetExecutor(tool)!.ExecuteAsync(
            new ToolInvokeRequest
            {
                ToolCallId = Guid.NewGuid().ToString("N"),
                ToolName = tool,
                Arguments = arguments,
                SessionId = sessionId,
            },
            CancellationToken.None);

    private static List<PendingQuestionEntry> ReadLedger(BridgeHarness harness)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(harness.LedgerPath));
                return JsonSerializer.Deserialize<List<PendingQuestionEntry>>(
                    document.RootElement.GetProperty("entries").GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>The subagents a test has running, and the primary session each was spawned from.</summary>
    internal sealed class FakeSubagentSessions : ISubagentSessionResolver
    {
        private readonly ConcurrentDictionary<string, string> _primaries = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);

        public FakeSubagentSessions Running(string subagent, string primary)
        {
            _primaries[subagent] = primary;
            _active[subagent] = 0;
            return this;
        }

        public void Finish(string subagent) => _active.TryRemove(subagent, out _);

        public bool IsSubagentSession(string sessionId) => sessionId.StartsWith("subagent/", StringComparison.Ordinal);

        public bool IsActive(string sessionId) => _active.ContainsKey(sessionId);

        public string? ResolvePrimarySession(string sessionId) => _primaries.GetValueOrDefault(sessionId);
    }
}

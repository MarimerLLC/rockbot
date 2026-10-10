using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Skills;

namespace RockBot.Host.Tests;

/// <summary>
/// The consequential-action gate (#685): a run whose originating user message asked for nothing
/// may not change external systems. The calendar-mcp call is refused, not executed, and the model
/// is told to propose it instead; instructions, approvals, agent-local writes, reads, and
/// user-configured automation run as before.
/// </summary>
[TestClass]
public class ConsequentialActionGateTests
{
    private const string ContextOnly =
        "Here's the abstract for my Techorama talk on agent swarms. The talk doesn't exist yet.";
    private const string Instruction = "Schedule three prep blocks for the Techorama talk on my work calendar.";
    private const string CreateEvent = "calendar-mcp__create_event";

    // ── Classification ──────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("calendar-mcp__create_event", null, true)]
    [DataRow("calendar-mcp__update_event", null, true)]
    [DataRow("calendar-mcp__delete_event", null, true)]
    [DataRow("mail__send_mail", null, true)]
    [DataRow("mail__reply_to_message", null, true)]
    [DataRow("onedrive__upload_file", null, true)]
    [DataRow("todo__complete_task", null, true)]
    [DataRow("mcp_invoke_tool", "server_name=calendar-mcp, tool_name=create_event, arguments={}", true)]
    [DataRow("mcp_invoke_tool", """{"server_name":"mail","tool_name":"forward_message"}""", true)]
    [DataRow("schedule_task", null, true)]
    [DataRow("cancel_scheduled_task", null, true)]
    [DataRow("calendar-mcp__list_events", null, false)]
    [DataRow("mcp_invoke_tool", "server_name=calendar-mcp, tool_name=get_event", false)]
    [DataRow("file_write", "path=drafts/talk.md", false)]
    [DataRow("file_edit", "path=drafts/talk.md", false)]
    [DataRow("save_memory", null, false)]
    [DataRow("save_to_working_memory", null, false)]
    [DataRow("save_skill", null, false)]
    [DataRow("task_create", null, false)]
    [DataRow("report_progress", null, false)]
    [DataRow("spawn_subagent", null, false)]
    [DataRow("invoke_agent", null, false)]
    [DataRow("execute_python_script", null, false)]
    public void IsConsequential(string tool, string? args, bool expected)
    {
        Assert.AreEqual(expected, ConsequentialActions.IsConsequential(tool, args));
    }

    [TestMethod]
    public void ConfiguredNativeTool_IsConsequential()
    {
        Assert.IsTrue(ConsequentialActions.IsConsequential("send_sms", null, ["send_sms"]));
        Assert.IsFalse(ConsequentialActions.IsConsequential("send_sms", null));
    }

    [TestMethod]
    public void Refusal_NamesTheToolAndServer_AndAsksForAProposal()
    {
        var typed = ConsequentialActions.BuildRefusal(CreateEvent, null);
        Assert.AreEqual(
            "Not run: calendar-mcp__create_event would change calendar-mcp but the user did not ask for that. " +
            "Propose it to the user in one sentence (what, when, where) and wait for them to ask. " +
            "Do not retry this call or route it through another tool.",
            typed);

        var proxied = ConsequentialActions.BuildRefusal("mcp_invoke_tool", "server_name=mail, tool_name=send_mail");
        StringAssert.StartsWith(proxied, "Not run: send_mail (via mcp_invoke_tool) would change mail ");
        Assert.IsTrue(ConsequentialActions.IsRefusal(proxied));
    }

    [TestMethod]
    public void Scope_OnlyUserTurnLineage_NeedsAnInstruction()
    {
        Assert.IsFalse(new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.InformationOnly).AllowsExternalChanges);
        Assert.IsFalse(new ActionGateScope(RunOrigin.SubagentOfUserTurn, UserRequestKind.InformationOnly).AllowsExternalChanges);
        Assert.IsTrue(new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.Instruction).AllowsExternalChanges);
        Assert.IsTrue(new ActionGateScope(RunOrigin.Scheduled, UserRequestKind.InformationOnly).AllowsExternalChanges);
        Assert.IsTrue(new ActionGateScope(RunOrigin.A2A, UserRequestKind.InformationOnly).AllowsExternalChanges);
        Assert.IsTrue(ActionGateScope.Unknown.AllowsExternalChanges);

        Assert.AreEqual(RunOrigin.SubagentOfUserTurn,
            new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.InformationOnly).ForSubagent().Origin);
        Assert.AreEqual(RunOrigin.Scheduled, ActionGateScope.ForScheduledTask.ForSubagent().Origin);
    }

    [TestMethod]
    public void ResolveScope_ExplicitThenEnclosingThenUnknown()
    {
        var enclosing = new ActionGateScope(RunOrigin.SubagentOfUserTurn, UserRequestKind.InformationOnly);

        Assert.AreEqual(enclosing,
            AgentLoopRunner.ResolveActionGateScope(null, enclosing, UserRequestKind.Instruction),
            "a nested run (wisp LLM step, worker) inherits the enclosing run's classification");
        Assert.AreEqual(new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.Instruction),
            AgentLoopRunner.ResolveActionGateScope(ActionGateScope.ForUserTurn, enclosing, UserRequestKind.Instruction),
            "a user turn classifies its own message");
        Assert.AreEqual(ActionGateScope.Unknown,
            AgentLoopRunner.ResolveActionGateScope(null, null, UserRequestKind.InformationOnly));
    }

    [TestMethod]
    public void FromRelayedResults_FoldsTheSubagentScopeBackIntoTheUserTurn()
    {
        Assert.IsNull(ActionGateScope.FromRelayedResults([Result(null, null)]), "older results keep old behaviour");
        Assert.AreEqual(new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.InformationOnly),
            ActionGateScope.FromRelayedResults([Result("subagent-of-user-turn", "information-only")]));
        Assert.AreEqual(new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.Instruction),
            ActionGateScope.FromRelayedResults(
                [Result("subagent-of-user-turn", "information-only"), Result("subagent-of-user-turn", "instruction")]));
        Assert.AreEqual(RunOrigin.Scheduled,
            ActionGateScope.FromRelayedResults([Result("scheduled", null)])!.Origin);
    }

    // ── The gate in the loop (native and text-based dispatch) ───────────────

    [TestMethod]
    [DataRow(false, DisplayName = "native")]
    [DataRow(true, DisplayName = "text-based")]
    public async Task InformationOnlyUserTurn_McpWrite_IsRefused_NotExecuted(bool textBased)
    {
        var run = await RunWithToolAsync(textBased, Conversation(ContextOnly), CreateEvent,
            actionGate: ActionGateScope.ForUserTurn);

        Assert.AreEqual(0, run.Executed.Count, "the event must not be created");
        StringAssert.Contains(run.ToolResult, "Not run: calendar-mcp__create_event would change calendar-mcp");
        StringAssert.Contains(run.ToolResult, "Propose it to the user in one sentence");
        var call = run.Ledger.Single();
        Assert.AreEqual(CreateEvent, call.Name);
        Assert.IsFalse(call.Succeeded, "a refused call is recorded as not having happened");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native")]
    [DataRow(true, DisplayName = "text-based")]
    public async Task InstructionUserTurn_McpWrite_Runs(bool textBased)
    {
        var run = await RunWithToolAsync(textBased, Conversation(Instruction), CreateEvent,
            actionGate: ActionGateScope.ForUserTurn);

        Assert.AreEqual(1, run.Executed.Count);
        Assert.AreEqual("created", run.ToolResult);
    }

    [TestMethod]
    public async Task YesAfterAnAgentProposal_IsAnInstruction()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "You are a test agent."),
            new(ChatRole.User, ContextOnly),
            new(ChatRole.Assistant, "Noted. Want me to block two prep sessions on Oct 12 and Oct 19?"),
            new(ChatRole.User, "yes"),
        ];

        var run = await RunWithToolAsync(false, messages, CreateEvent, actionGate: ActionGateScope.ForUserTurn);

        Assert.AreEqual(1, run.Executed.Count, "\"yes\" accepts the proposal");
    }

    [TestMethod]
    public async Task SubagentOfInformationOnlyTurn_McpWrite_IsRefused()
    {
        // What SubagentRunner passes: the spawning turn's scope, marked as a subagent's.
        var run = await RunWithToolAsync(false,
            Conversation("Plan and place prep, validation and rehearsal blocks for the Techorama talk."),
            CreateEvent,
            actionGate: new ActionGateScope(RunOrigin.SubagentOfUserTurn, UserRequestKind.InformationOnly));

        Assert.AreEqual(0, run.Executed.Count,
            "the subagent's own (imperative) task description must not launder the user's information-only message");
        StringAssert.Contains(run.ToolResult, "Not run:");
    }

    [TestMethod]
    public async Task NestedRunWithoutExplicitScope_InheritsTheEnclosingScope()
    {
        // A wisp LLM step or worker calls RunAsync from inside its parent's tool call.
        var gate = new ConsequentialActionGate(Options.Create(new AgentHostOptions()));
        using var parent = ActionGateContext.Set(
            new ActionGateScope(RunOrigin.SubagentOfUserTurn, UserRequestKind.InformationOnly), gate);

        var run = await RunWithToolAsync(false, Conversation("Create the event described below."), CreateEvent,
            actionGate: null);

        Assert.AreEqual(0, run.Executed.Count);
    }

    [TestMethod]
    [DataRow("file_write", DisplayName = "file_write to drafts/")]
    [DataRow("calendar-mcp__list_events", DisplayName = "read-only MCP call")]
    [DataRow("save_to_working_memory", DisplayName = "working memory")]
    public async Task InformationOnlyUserTurn_AgentLocalOrReadOnlyCall_Runs(string tool)
    {
        var run = await RunWithToolAsync(false, Conversation(ContextOnly), tool,
            actionGate: ActionGateScope.ForUserTurn,
            args: new Dictionary<string, object?> { ["path"] = "drafts/techorama-notes.md" });

        Assert.AreEqual(1, run.Executed.Count);
    }

    [TestMethod]
    [DataRow(RunOrigin.Scheduled)]
    [DataRow(RunOrigin.A2A)]
    [DataRow(RunOrigin.Unknown)]
    public async Task ConfiguredAutomation_McpWrite_Runs(RunOrigin origin)
    {
        // A patrol prompt reads as information-only; it still acts, as the user configured it to.
        var run = await RunWithToolAsync(false, Conversation("Morning calendar patrol."), CreateEvent,
            actionGate: new ActionGateScope(origin));

        Assert.AreEqual(1, run.Executed.Count);
    }

    [TestMethod]
    public async Task GateDisabled_McpWrite_Runs()
    {
        var hostOptions = new AgentHostOptions();
        hostOptions.ConsequentialActionGate.Enabled = false;

        var run = await RunWithToolAsync(false, Conversation(ContextOnly), CreateEvent,
            actionGate: ActionGateScope.ForUserTurn, hostOptions: hostOptions);

        Assert.AreEqual(1, run.Executed.Count);
    }

    [TestMethod]
    public async Task ScopeDoesNotLeakPastTheRun()
    {
        await RunWithToolAsync(false, Conversation(ContextOnly), CreateEvent, actionGate: ActionGateScope.ForUserTurn);

        Assert.IsNull(ActionGateContext.Scope);
        Assert.IsNull(ActionGateContext.Check(CreateEvent, null), "no run → nothing to gate");
    }

    // ── Evaluator backstop ──────────────────────────────────────────────────

    [TestMethod]
    public void Evaluator_InformationOnly_RelayedExternalWrite_IsFlagged()
    {
        var relayed = new RelayedSubagentWork("abc123",
        [
            new SubagentToolCallSummary("calendar-mcp__list_events", true, false, null),
            new SubagentToolCallSummary("mcp_invoke_tool → create_event", true, true, "tool_name=create_event"),
            new SubagentToolCallSummary("file_write", true, true, "path=drafts/plan.md"),
        ]);
        var input = new CompletionEvalTriggers.EvaluatorInput(
            [ContextOnly], null, ContextOnly, "Seven prep blocks are scheduled and verified.",
            [new LoopToolCall("get_from_working_memory", "key=subagent/abc123/result", true)],
            CompletionEvalTrigger.SubagentSynthesis, [relayed], UserRequestKind.InformationOnly,
            ExternalChangesNeedRequest: true);

        var text = CompletionEvalTriggers.BuildEvaluatorUserMessage(input);

        StringAssert.Contains(text, "## External changes the user did not ask for");
        StringAssert.Contains(text, "- subagent abc123: mcp_invoke_tool → create_event");
        Assert.IsFalse(text.Contains("subagent abc123: file_write"), "drafts are agent-local");
        StringAssert.Contains(text, "unrequested external change");

        var changes = CompletionEvalTriggers.ExternalChanges(input.ToolCalls, [relayed]);
        var guidance = CompletionEvalTriggers.RepromptGuidance(
            CompletionEvalTrigger.SubagentSynthesis, input.ToolCalls, UserRequestKind.InformationOnly, changes);
        StringAssert.Contains(guidance, "did not ask for these external changes: subagent abc123: mcp_invoke_tool → create_event");
        StringAssert.Contains(guidance, "so they can keep or undo it");
        StringAssert.Contains(guidance, "do not hide it");
    }

    [TestMethod]
    public void Evaluator_NotFlagged_ForInstructions_ScheduledRuns_OrRefusedCalls()
    {
        var write = new LoopToolCall(CreateEvent, null, true);
        var refused = new LoopToolCall(CreateEvent, null, false);

        string Build(UserRequestKind kind, bool needsRequest, LoopToolCall call) =>
            CompletionEvalTriggers.BuildEvaluatorUserMessage(new CompletionEvalTriggers.EvaluatorInput(
                [ContextOnly], null, null, "Done.", [call], CompletionEvalTrigger.SideEffect,
                UserAskedFor: kind, ExternalChangesNeedRequest: needsRequest));

        Assert.IsTrue(Build(UserRequestKind.InformationOnly, true, write).Contains("External changes the user did not ask for"));
        Assert.IsFalse(Build(UserRequestKind.Instruction, true, write).Contains("External changes the user did not ask for"));
        Assert.IsFalse(Build(UserRequestKind.InformationOnly, false, write).Contains("External changes the user did not ask for"),
            "a scheduled task's own prompt can read as information-only and still legitimately write");
        Assert.IsFalse(Build(UserRequestKind.InformationOnly, true, refused).Contains("External changes the user did not ask for"),
            "a call the gate refused changed nothing");
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static SubagentResultMessage Result(string? origin, string? askedFor) => new()
    {
        TaskId = "t1",
        SubagentSessionId = "subagent-t1",
        PrimarySessionId = "s1",
        Output = "done",
        IsSuccess = true,
        Timestamp = DateTimeOffset.UtcNow,
        RunOrigin = origin,
        UserAskedFor = askedFor,
    };

    private static List<ChatMessage> Conversation(string userMessage) =>
    [
        new(ChatRole.System, "You are a test agent."),
        new(ChatRole.User, userMessage),
    ];

    private sealed record RunResult(List<string> Executed, string ToolResult, IReadOnlyList<LoopToolCall> Ledger);

    private static async Task<RunResult> RunWithToolAsync(
        bool textBased,
        List<ChatMessage> messages,
        string tool,
        ActionGateScope? actionGate,
        Dictionary<string, object?>? args = null,
        AgentHostOptions? hostOptions = null)
    {
        var model = new ToolThenTextChatClient(
            new FunctionCallContent("call-1", tool, args ?? new Dictionary<string, object?> { ["title"] = "Prep" }),
            "Reply.");
        var executed = new List<string>();
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((string? title = null, string? path = null) =>
            {
                executed.Add(tool);
                return "created";
            }, tool)]
        };
        var diagnostics = new LoopDiagnostics();
        var runner = CreateRunner(model, textBased, hostOptions ?? new AgentHostOptions());

        await runner.RunAsync(messages, options, "s1",
            enableFollowUp: false, enableCompletionEval: false, diagnostics: diagnostics,
            cancellationToken: CancellationToken.None, actionGate: actionGate);

        return new RunResult(executed, model.ToolResultSeen ?? string.Empty,
            diagnostics.ToolCallLedger?.Snapshot() ?? []);
    }

    private static AgentLoopRunner CreateRunner(IChatClient model, bool textBased, AgentHostOptions host)
    {
        var profileOpts = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), "rockbot-action-gate-" + Guid.NewGuid().ToString("N"))
        });
        Directory.CreateDirectory(profileOpts.Value.BasePath);
        var clock = new AgentClock(new ConfigurationBuilder().Build(), profileOpts, NullLogger<AgentClock>.Instance);
        var behavior = new ModelBehavior { UseTextBasedToolCalling = textBased };
        var hostOptions = Options.Create(host);
        var workingMemory = new StubWorkingMemory();

        IChatClient client = textBased
            ? model
            : new RockBotFunctionInvokingChatClient(model, null, null, behavior,
                new LlmCostEstimator(
                    Options.Create(new LlmPricingOptions
                    {
                        ConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "p.json")
                    }),
                    NullLogger<LlmCostEstimator>.Instance),
                workingMemory, hostOptions, NullLogger.Instance);

        return new AgentLoopRunner(
            new ChatClientLlm(client),
            workingMemory,
            behavior,
            new StubFeedbackStore(),
            clock,
            hostOptions,
            new StubSkillStore(),
            Array.Empty<IServiceSearchIndex>(),
            new StubConversationMemory(),
            NullLogger<AgentLoopRunner>.Instance,
            consequentialActionGate: new ConsequentialActionGate(hostOptions));
    }

    /// <summary>First call returns a tool call; later calls record the tool result and return text.</summary>
    private sealed class ToolThenTextChatClient(FunctionCallContent call, string finalText) : IChatClient
    {
        private int _calls;

        public string? ToolResultSeen { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));

            var list = messages.ToList();
            ToolResultSeen ??= list
                .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                .Select(r => r.Result?.ToString())
                .LastOrDefault(r => r is not null)
                ?? list.LastOrDefault(m => m.Role == ChatRole.User && m.Text?.StartsWith("[Tool result", StringComparison.Ordinal) == true)?.Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, finalText)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ChatClientLlm(IChatClient client) : ILlmClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct) =>
            client.GetResponseAsync(messages, options, ct);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options, CancellationToken ct) =>
            client.GetResponseAsync(messages, options, ct);
    }

    private sealed class StubFeedbackStore : IFeedbackStore
    {
        public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
        public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
    }

    private sealed class StubConversationMemory : IConversationMemory
    {
        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationTurn>>([]);
        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubWorkingMemory : IWorkingMemory
    {
        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null) => Task.CompletedTask;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }

    private sealed class StubSkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
    }
}

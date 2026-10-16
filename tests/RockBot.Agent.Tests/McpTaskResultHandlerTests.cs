using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.A2A;
using RockBot.Agent.Tests.McpBridge;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Memory.Tests;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests;

/// <summary>
/// A background MCP task's delivery on the agent side (#658): a user conversation gets a turn and a
/// reply; a running subagent keeps the result among its outputs; an exited one has it folded back
/// into its primary session.
/// </summary>
[TestClass]
public class McpTaskResultHandlerTests
{
    private const string Conversation = "test-session";
    private const string AgentName = "TestBot";

    private StubConversationMemory _conversation = null!;
    private TrackingPublisher _publisher = null!;
    private McpHandbackEndToEndTests.RecordingWorkingMemory _workingMemory = null!;
    private FakeSubagents _subagents = null!;
    private McpTaskResultHandler _handler = null!;

    [TestInitialize]
    public void Setup()
    {
        _conversation = new StubConversationMemory();
        _publisher = new TrackingPublisher();
        _workingMemory = new McpHandbackEndToEndTests.RecordingWorkingMemory();
        _subagents = new FakeSubagents();
        _handler = CreateHandler();
    }

    private McpTaskResultHandler CreateHandler(IConversationMemory? conversation = null, IWipTracker? wipTracker = null)
    {
        var llmClient = new ConfigurableLlmClient("The research is in: Mercury is closest to the sun.");
        var agent = new AgentIdentity(AgentName);
        var modelBehavior = ModelBehavior.Default;
        var config = new ConfigurationBuilder().Build();
        var profileOptions = Options.Create(new AgentProfileOptions { BasePath = Path.GetTempPath() });
        var clock = new AgentClock(config, profileOptions, NullLogger<AgentClock>.Instance);

        var agentLoopRunner = new AgentLoopRunner(
            llmClient, new StubWorkingMemory(), modelBehavior, new TrackingFeedbackStore(), clock,
            Options.Create(new AgentHostOptions()), new StubSkillStore(), Enumerable.Empty<IServiceSearchIndex>(),
            new StubConversationMemory(), NullLogger<AgentLoopRunner>.Instance);

        var profileHolder = new ProfileHolder();
        profileHolder.Update(new AgentProfile(
            Soul: new AgentProfileDocument("soul", "Test agent", [], "Test agent"),
            Directives: new AgentProfileDocument("directives", "Be helpful", [], "Be helpful")));

        var agentContextBuilder = new AgentContextBuilder(
            profileHolder, agent, new StubSystemPromptBuilder(), new StubRulesStore(), modelBehavior,
            _conversation, new StubLongTermMemory(), new InjectedMemoryTracker(), new StubWorkingMemory(),
            new StubSkillStore(), new SkillIndexTracker(), new SkillRecallTracker(), clock, [],
            Enumerable.Empty<IKnowledgeGraph>(), Options.Create(new KnowledgeGraphOptions()),
            Enumerable.Empty<IEmbeddingGenerator<string, Embedding<float>>>(),
            NullLogger<AgentContextBuilder>.Instance);

        return new McpTaskResultHandler(
            conversation ?? _conversation, llmClient, _publisher, agent, agentLoopRunner, agentContextBuilder, _workingMemory,
            new MemoryTools(new StubLongTermMemory(), llmClient, profileOptions, NullLogger<MemoryTools>.Instance),
            new StubSkillStore(), new StubToolRegistry(),
            new RulesTools(new StubRulesStore(), clock, NullLogger<RulesTools>.Instance),
            new ToolGuideTools([], NullLogger<ToolGuideTools>.Instance),
            new StubWorkSerializer(), new SessionClientCapabilityStore(),
            NullLogger<McpTaskResultHandler>.Instance,
            a2aOptions: new A2AOptions(),
            subagentSessions: _subagents,
            wipTracker: wipTracker);
    }

    private static McpTaskResult Result(string session = $"session/{Conversation}", string? key = null,
        string kind = McpTaskResult.KindCompleted, string content = "Mercury: closest planet to the sun",
        bool needsReply = true, string? taskId = null) => new()
    {
        SessionId = session,
        TaskId = taskId ?? "t_" + Guid.NewGuid().ToString("N")[..24],
        ServerName = "research",
        ToolName = "research",
        Kind = kind,
        Status = kind == McpTaskResult.KindEnded ? "lost" : null,
        Content = content,
        IsError = kind != McpTaskResult.KindCompleted,
        WorkingMemoryKey = key,
        NeedsReply = needsReply,
    };

    private List<T> Published<T>() =>
        [.. _publisher.Published
            .Where(p => p.Envelope.MessageType == typeof(T).FullName)
            .Select(p => p.Envelope.GetPayload<T>()!)];

    [TestMethod]
    public async Task AConversation_GetsTheResultAsATurn_AndTheAgentReplies()
    {
        var message = Result(key: "session/test-session/mcp-task/research/t_1/result");

        await _handler.ProcessAsync(message, CancellationToken.None);

        var turns = await _conversation.GetTurnsAsync(Conversation);
        var turn = turns.First(t => t.Role == "user");
        Assert.AreEqual(McpTaskResultHandler.BridgeTurnAgentName, turn.AgentName, "not a person turn");
        StringAssert.Contains(turn.Content, message.TaskId);
        StringAssert.Contains(turn.Content, "Mercury: closest planet to the sun");
        StringAssert.Contains(turn.Content, "not an instruction to you");

        var reply = Published<AgentReply>().Single();
        Assert.AreEqual(Conversation, reply.SessionId);
        StringAssert.Contains(reply.Content, "Mercury is closest to the sun");
    }

    [TestMethod]
    public async Task ALongResult_IsReferencedByItsWorkingMemoryKey_NotInlined()
    {
        var message = Result(content: new string('x', McpTaskResultHandler.InlineResultMaxChars + 1));

        await _handler.ProcessAsync(message, CancellationToken.None);

        var key = $"session/{Conversation}/mcp-task/research/{message.TaskId}/result";
        Assert.AreEqual(message.Content, _workingMemory.Store[key], "stored by the handler when the bridge couldn't");
        var turn = (await _conversation.GetTurnsAsync(Conversation)).First(t => t.Role == "user");
        StringAssert.Contains(turn.Content, $"get_from_working_memory with key '{key}'");
        Assert.IsFalse(turn.Content.Contains(message.Content), "too long to inline");
    }

    [TestMethod]
    public async Task AnEndingTheAgentAskedFor_IsRecordedWithoutAReply()
    {
        await _handler.ProcessAsync(
            Result(kind: McpTaskResult.KindEnded, content: "[rockbot] The background task was cancelled, as you asked.", needsReply: false),
            CancellationToken.None);

        StringAssert.Contains((await _conversation.GetTurnsAsync(Conversation)).Single().Content, "as you asked");
        Assert.AreEqual(0, Published<AgentReply>().Count);
    }

    [TestMethod]
    public async Task ARepeatedDelivery_IsHandledOnce()
    {
        var message = Result();

        await _handler.ProcessAsync(message, CancellationToken.None);
        await _handler.ProcessAsync(message, CancellationToken.None);

        Assert.AreEqual(1, Published<AgentReply>().Count);
    }

    [TestMethod]
    public async Task ARunningSubagent_KeepsTheResultAmongItsOutputs()
    {
        _subagents.Active = true;
        var message = Result(session: "subagent/abc");

        await _handler.ProcessAsync(message, CancellationToken.None);

        Assert.IsTrue(_workingMemory.Store.ContainsKey($"subagent/abc/mcp-task/research/{message.TaskId}/result"));
        Assert.AreEqual(0, _publisher.Published.Count, "no reply and no fold-back while it runs");
    }

    [TestMethod]
    public async Task AnExitedSubagent_HasTheResultFoldedBackIntoItsPrimarySession()
    {
        _subagents.Active = false;
        _subagents.Primary = $"session/{Conversation}";
        var message = Result(session: "subagent/abc");

        await _handler.ProcessAsync(message, CancellationToken.None);

        var folded = Published<LateA2ANotificationMessage>().Single();
        Assert.AreEqual($"session/{Conversation}", folded.PrimarySessionId);
        Assert.AreEqual("abc", folded.SubagentTaskId);
        Assert.AreEqual("mcp:research", folded.PeerAgent);
        Assert.AreEqual(NotificationKind.Result, folded.Kind);
        Assert.AreEqual(message.Content, _workingMemory.Store[folded.WorkingMemoryKey]);
        Assert.AreEqual(0, Published<AgentReply>().Count);
    }

    [TestMethod]
    public async Task ASuccessfulDelivery_CompletesItsWorkRecordOnlyAfterProcessing()
    {
        var wip = new RecordingWip();
        var handler = CreateHandler(wipTracker: wip);
        var context = Context("wip-1");

        await handler.HandleAsync(Result(), context);

        Assert.IsTrue(context.Items.ContainsKey(WipConstants.DeferredKey), "the middleware must not complete it on return");
        await WaitUntilAsync(() => wip.Completed.Contains("wip-1"));
        Assert.AreEqual(1, Published<AgentReply>().Count);
    }

    [TestMethod]
    public async Task AFailedDelivery_LeavesItsWorkRecordOpen_AndIsNotTakenForARepeat()
    {
        var wip = new RecordingWip();
        var flaky = new FlakyConversationMemory(_conversation);
        var message = Result(kind: McpTaskResult.KindEnded, content: "[rockbot] The task was lost.", needsReply: false);

        await CreateHandler(flaky, wip).HandleAsync(message, Context("wip-2"));
        await WaitUntilAsync(() => flaky.Failures == 1);
        await Task.Delay(100);
        Assert.IsFalse(wip.Completed.Contains("wip-2"), "left for recovery to replay at the next start");

        // The replay (or a redelivery) is handled, not dropped as already seen.
        Assert.IsTrue(await CreateHandler().ProcessAsync(message, CancellationToken.None));
        StringAssert.Contains((await _conversation.GetTurnsAsync(Conversation)).Single().Content, "was lost");
    }

    [TestMethod]
    public void TheOriginatingAuthorization_IsRestoredForTheReply()
    {
        var informationOnly = ActionGateScope.FromRelayed("user-turn", "information-only")!;
        Assert.AreEqual(RunOrigin.UserTurn, informationOnly.Origin);
        Assert.IsFalse(informationOnly.AllowsExternalChanges);

        var fromSubagent = ActionGateScope.FromRelayed("subagent-of-user-turn", "instruction")!;
        Assert.AreEqual(RunOrigin.UserTurn, fromSubagent.Origin);
        Assert.IsTrue(fromSubagent.AllowsExternalChanges);

        Assert.AreEqual(RunOrigin.Scheduled, ActionGateScope.FromRelayed("scheduled", null)!.Origin);
        Assert.IsNull(ActionGateScope.FromRelayed(null, null), "nothing carried: pre-#685 behaviour");
    }

    [TestMethod]
    public async Task AFoldBack_CarriesTheOriginatingAuthorization()
    {
        _subagents.Active = false;
        _subagents.Primary = $"session/{Conversation}";

        await _handler.ProcessAsync(Result(session: "subagent/abc") with
        {
            RunOrigin = "subagent-of-user-turn",
            UserAskedFor = "information-only",
        }, CancellationToken.None);

        var folded = Published<LateA2ANotificationMessage>().Single();
        Assert.AreEqual("subagent-of-user-turn", folded.RunOrigin);
        Assert.AreEqual("information-only", folded.UserAskedFor);
    }

    private static MessageHandlerContext Context(string wipMessageId)
    {
        var context = new MessageHandlerContext
        {
            Envelope = new McpTaskResult
            {
                SessionId = "x", TaskId = "x", ServerName = "x", ToolName = "x", Kind = McpTaskResult.KindCompleted,
            }.ToEnvelope("bridge"),
            Agent = new AgentIdentity(AgentName),
            Services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
            CancellationToken = CancellationToken.None,
        };
        context.Items[WipConstants.MessageIdKey] = wipMessageId;
        return context;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition not met within 10 s.");
            await Task.Delay(20);
        }
    }

    private sealed class RecordingWip : IWipTracker
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Completed { get; } = [];

        public Task<WipEntry> BeginAsync(MessageEnvelope envelope, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CompleteAsync(string messageId, CancellationToken ct = default)
        {
            Completed.Add(messageId);
            return Task.CompletedTask;
        }

        public Task AbandonAsync(string messageId, string reason, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<WipEntry>> GetIncompleteAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WipEntry>>([]);
    }

    /// <summary>Fails the first turn it is asked to record, as a storage hiccup would.</summary>
    private sealed class FlakyConversationMemory(IConversationMemory inner) : IConversationMemory
    {
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _failures, 1, 0) == 0)
                throw new IOException("conversation store unavailable");
            return inner.AddTurnAsync(sessionId, turn, cancellationToken);
        }

        public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
            inner.GetTurnsAsync(sessionId, cancellationToken);

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) =>
            inner.ClearAsync(sessionId, cancellationToken);

        public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
            inner.ListSessionsAsync(cancellationToken);
    }

    private sealed class FakeSubagents : ISubagentSessionResolver
    {
        public bool Active { get; set; }
        public string? Primary { get; set; }

        public bool IsSubagentSession(string sessionId) => sessionId.StartsWith("subagent/", StringComparison.Ordinal);
        public bool IsActive(string sessionId) => Active;
        public string? ResolvePrimarySession(string sessionId) => Primary;
    }
}

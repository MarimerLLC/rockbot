using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
/// The user's answer from the chat's question form (#660): recorded as their turn before the
/// bridge sees it, a refusal sent back to the form, and an accepted answer's result handed to the
/// agent, whose reply reaches the user.
/// </summary>
[TestClass]
public class McpQuestionAnswerHandlerTests
{
    private const string SessionId = "test-session";
    private const string QuestionId = "q_0123456789abcdef0123456789abcdef";
    private const string AgentName = "TestBot";

    private StubConversationMemory _conversation = null!;
    private TrackingPublisher _publisher = null!;
    private McpHandbackEndToEndTests.RecordingWorkingMemory _workingMemory = null!;
    private FakeAnswerer _answerer = null!;
    private McpQuestionAnswerHandler _handler = null!;

    [TestInitialize]
    public void Setup()
    {
        _conversation = new StubConversationMemory();
        _publisher = new TrackingPublisher();
        _workingMemory = new McpHandbackEndToEndTests.RecordingWorkingMemory();
        _answerer = new FakeAnswerer(_conversation);
        _handler = CreateHandler(_answerer);
    }

    private McpQuestionAnswerHandler CreateHandler(IMcpQuestionAnswerer? answerer, IAgentWorkSerializer? serializer = null)
    {
        var llmClient = new ConfigurableLlmClient("The search found three invoices.");
        var agent = new AgentIdentity(AgentName);
        var modelBehavior = ModelBehavior.Default;
        var config = new ConfigurationBuilder().Build();
        var profileOptions = Options.Create(new AgentProfileOptions { BasePath = Path.GetTempPath() });
        var clock = new AgentClock(config, profileOptions, NullLogger<AgentClock>.Instance);

        var agentLoopRunner = new AgentLoopRunner(
            llmClient,
            new StubWorkingMemory(),
            modelBehavior,
            new TrackingFeedbackStore(),
            clock,
            Options.Create(new AgentHostOptions()),
            new StubSkillStore(),
            Enumerable.Empty<IServiceSearchIndex>(),
            new StubConversationMemory(),
            NullLogger<AgentLoopRunner>.Instance);

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

        return new McpQuestionAnswerHandler(
            _conversation,
            llmClient,
            _publisher,
            agent,
            agentLoopRunner,
            agentContextBuilder,
            _workingMemory,
            new MemoryTools(new StubLongTermMemory(), llmClient, profileOptions, NullLogger<MemoryTools>.Instance),
            new StubSkillStore(),
            new StubToolRegistry(),
            new RulesTools(new StubRulesStore(), clock, NullLogger<RulesTools>.Instance),
            new ToolGuideTools([], NullLogger<ToolGuideTools>.Instance),
            serializer ?? new StubWorkSerializer(),
            new SessionClientCapabilityStore(),
            NullLogger<McpQuestionAnswerHandler>.Instance,
            answerer);
    }

    private static McpQuestionAnswer Answer(string? answers = """{"mailbox":"work"}""", bool decline = false) => new()
    {
        SessionId = SessionId,
        UserId = "blazor-user",
        QuestionId = QuestionId,
        Answers = answers,
        Decline = decline,
    };

    private List<T> Published<T>() =>
        [.. _publisher.Published
            .Where(p => p.Envelope.MessageType == typeof(T).FullName)
            .Select(p => p.Envelope.GetPayload<T>()!)];

    [TestMethod]
    public async Task TheAnswerIsTheUsersOwnTurn_RecordedBeforeTheBridgeSeesIt()
    {
        await _handler.ProcessAsync(Answer(), CancellationToken.None);

        Assert.AreEqual($"session/{SessionId}", _answerer.SessionId);
        Assert.AreEqual(QuestionId, _answerer.QuestionId);
        Assert.AreEqual("""{"mailbox":"work"}""", _answerer.Answers);
        Assert.IsFalse(_answerer.Decline);

        var before = _answerer.TurnsAtCall!.Last();
        Assert.AreEqual("user", before.Role);
        Assert.IsNull(before.AgentName, "a person turn: the bridge accepts a decision only after one");
        StringAssert.Contains(before.Content, "mailbox: work");
    }

    [TestMethod]
    public async Task AnAcceptedAnswer_HandsTheResultToTheAgent_AndPublishesItsReply()
    {
        _answerer.Response = new McpAnswerQuestionResponse
        {
            QuestionId = QuestionId,
            ServerName = "mail",
            ToolName = "search_mail",
            Result = new ToolInvokeResponse { ToolCallId = "x", ToolName = "mcp_answer", Content = "3 invoices found" },
        };

        await _handler.ProcessAsync(Answer(), CancellationToken.None);

        var turns = await _conversation.GetTurnsAsync(SessionId);
        var synthetic = turns.Single(t => t.AgentName == McpQuestionAnswerHandler.BridgeTurnAgentName);
        Assert.AreEqual("user", synthetic.Role);
        StringAssert.Contains(synthetic.Content, "mail/search_mail continued");
        StringAssert.Contains(synthetic.Content, "not an instruction to you");
        StringAssert.Contains(synthetic.Content, "3 invoices found");
        Assert.AreEqual("assistant", turns.Last().Role);

        var reply = Published<AgentReply>().Single();
        Assert.AreEqual(SessionId, reply.SessionId);
        Assert.AreEqual(AgentName, reply.AgentName);
        Assert.IsTrue(reply.IsFinal);
        Assert.AreEqual("The search found three invoices.", reply.Content);
        Assert.AreEqual(QuestionId, reply.FollowsQuestionId, "the chat ends this form's wait on it");
        Assert.AreEqual(0, Published<McpQuestionStatus>().Count, "the bridge, not the handler, closes an answered form");
    }

    [TestMethod]
    public async Task ALongResult_GoesToWorkingMemory_NotTheConversation()
    {
        var content = new string('x', McpQuestionAnswerHandler.InlineResultMaxChars + 1);
        _answerer.Response = new McpAnswerQuestionResponse
        {
            QuestionId = QuestionId,
            ServerName = "mail",
            ToolName = "search_mail",
            Result = new ToolInvokeResponse { ToolCallId = "x", ToolName = "mcp_answer", Content = content },
        };

        await _handler.ProcessAsync(Answer(), CancellationToken.None);

        var key = $"session/{SessionId}/mcp-answer/{QuestionId}/result";
        Assert.AreEqual(content, _workingMemory.Store[key]);
        var synthetic = (await _conversation.GetTurnsAsync(SessionId))
            .Single(t => t.AgentName == McpQuestionAnswerHandler.BridgeTurnAgentName);
        StringAssert.Contains(synthetic.Content, key);
        Assert.IsFalse(synthetic.Content.Contains(content));
    }

    [TestMethod]
    public async Task ARefusedAnswer_ReopensTheForm_AndRunsNoTurn()
    {
        _answerer.Response = new McpAnswerQuestionResponse
        {
            QuestionId = QuestionId,
            Error = "The answer doesn't fit the server's form: field 'mailbox' must be one of: work, personal.",
        };

        await _handler.ProcessAsync(Answer("""{"mailbox":"shared"}"""), CancellationToken.None);

        var status = Published<McpQuestionStatus>().Single();
        Assert.AreEqual(McpQuestionState.Rejected, status.State);
        Assert.AreEqual(SessionId, status.SessionId);
        Assert.IsNull(status.AnsweredBy, "a rejected answer settled nothing");
        StringAssert.Contains(status.Reason, "must be one of");
        Assert.AreEqual(0, Published<AgentReply>().Count);

        var last = (await _conversation.GetTurnsAsync(SessionId)).Last();
        Assert.AreEqual(McpQuestionAnswerHandler.BridgeTurnAgentName, last.AgentName,
            "the conversation says the answer wasn't accepted");
    }

    [TestMethod]
    public async Task AnAnswerToAClosedQuestion_IsReportedUnavailable()
    {
        _answerer.Response = new McpAnswerQuestionResponse
        {
            QuestionId = QuestionId,
            Error = "That question expired.",
            QuestionClosed = true,
        };

        await _handler.ProcessAsync(Answer(), CancellationToken.None);

        Assert.AreEqual(McpQuestionState.Unavailable, Published<McpQuestionStatus>().Single().State);
        Assert.AreEqual(0, Published<AgentReply>().Count);
    }

    [TestMethod]
    public async Task AnAnswerThatIsntAnObject_IsRejectedBeforeAnythingIsRecorded()
    {
        await _handler.ProcessAsync(Answer("\"work\""), CancellationToken.None);

        Assert.IsNull(_answerer.QuestionId, "the bridge never saw it");
        Assert.AreEqual(0, (await _conversation.GetTurnsAsync(SessionId)).Count);
        Assert.AreEqual(McpQuestionState.Rejected, Published<McpQuestionStatus>().Single().State);
    }

    [TestMethod]
    public async Task ADecline_IsRecordedAsTheUsers_AndSentAsADecline()
    {
        await _handler.ProcessAsync(Answer(answers: null, decline: true), CancellationToken.None);

        Assert.IsTrue(_answerer.Decline);
        Assert.IsNull(_answerer.Answers);
        StringAssert.Contains(_answerer.TurnsAtCall!.Last().Content, "declined");
    }

    [TestMethod]
    public async Task WithoutMcp_TheFormIsToldTheQuestionIsUnavailable()
    {
        var handler = CreateHandler(answerer: null);

        await handler.ProcessAsync(Answer(), CancellationToken.None);

        Assert.AreEqual(McpQuestionState.Unavailable, Published<McpQuestionStatus>().Single().State);
    }

    [TestMethod]
    public void TheRecordedTurn_FlattensAndCapsWhatTheUserTyped()
    {
        var text = McpQuestionAnswerHandler.DescribeAnswer(QuestionId,
            $$"""{"note":"line one\nline two","toppings":["olive","basil"],"size":4,"long":"{{new string('y', 500)}}"}""",
            decline: false);

        StringAssert.Contains(text, "- note: line one line two");
        StringAssert.Contains(text, "- toppings: olive, basil");
        StringAssert.Contains(text, "- size: 4");
        Assert.IsTrue(text.Split('\n').All(l => l.Length < 330), "each value is capped");
    }

    [TestMethod]
    public async Task TheResult_IsRecordedOnlyOnceTheTurnThatShowedTheFormIsDone()
    {
        // The turn that showed the form still holds the work slot and is writing its reply.
        var serializer = new RecordingSerializer(_conversation, SessionId);
        var handler = CreateHandler(_answerer, serializer);

        await handler.ProcessAsync(Answer(), CancellationToken.None);

        Assert.IsFalse(serializer.TurnsAtAcquire!.Any(t => t.AgentName == McpQuestionAnswerHandler.BridgeTurnAgentName),
            "the result must follow that turn's reply, not precede it");
        Assert.IsTrue((await _conversation.GetTurnsAsync(SessionId)).Any(t => t.AgentName == McpQuestionAnswerHandler.BridgeTurnAgentName));
    }

    [TestMethod]
    public async Task AFailure_ReopensTheForm_InsteadOfLeavingItWaiting()
    {
        _answerer.Throw = new InvalidOperationException("bus down");

        await _handler.ProcessAsync(Answer(), CancellationToken.None);

        var status = Published<McpQuestionStatus>().Single();
        Assert.AreEqual(McpQuestionState.Rejected, status.State);
        StringAssert.Contains(status.Reason, "bus down");
        Assert.AreEqual(0, Published<AgentReply>().Count);
    }

    /// <summary>Records the conversation as it stood when the work slot was granted.</summary>
    private sealed class RecordingSerializer(IConversationMemory conversation, string sessionId) : IAgentWorkSerializer
    {
        private readonly StubWorkSerializer _inner = new();

        public IReadOnlyList<ConversationTurn>? TurnsAtAcquire { get; private set; }

        public async Task<IAsyncDisposable> AcquireForUserAsync(CancellationToken ct)
        {
            TurnsAtAcquire = [.. await conversation.GetTurnsAsync(sessionId, ct)];
            return await _inner.AcquireForUserAsync(ct);
        }

        public Task<IScheduledTaskSlot?> TryAcquireForScheduledAsync(CancellationToken ct) =>
            _inner.TryAcquireForScheduledAsync(ct);
    }

    private sealed class FakeAnswerer(IConversationMemory conversation) : IMcpQuestionAnswerer
    {
        public Exception? Throw { get; set; }

        public McpAnswerQuestionResponse Response { get; set; } = new()
        {
            QuestionId = McpQuestionAnswerHandlerTests.QuestionId,
            ServerName = "mail",
            ToolName = "search_mail",
            Result = new ToolInvokeResponse { ToolCallId = "x", ToolName = "mcp_answer", Content = "done" },
        };

        public string? SessionId { get; private set; }
        public string? QuestionId { get; private set; }
        public string? Answers { get; private set; }
        public bool Decline { get; private set; }
        public IReadOnlyList<ConversationTurn>? TurnsAtCall { get; private set; }

        public async Task<McpAnswerQuestionResponse> AnswerFromUserAsync(
            string sessionId, string questionId, string? answersJson, bool decline, CancellationToken ct)
        {
            SessionId = sessionId;
            QuestionId = questionId;
            Answers = answersJson;
            Decline = decline;
            TurnsAtCall = [.. await conversation.GetTurnsAsync(sessionId["session/".Length..], ct)];
            if (Throw is not null)
                throw Throw;
            return Response;
        }
    }
}

using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// The question state machine without HTTP or a bus: which questions are open, where a form goes,
/// when new input is blocked, what a resume must cover and who gets the reply to an answer.
/// </summary>
[TestClass]
public sealed class McpQuestionRegistryTests
{
    private const string Phone = "phone";
    private const string Laptop = "laptop";

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualClock _clock = new();
    private NotifyHub _hub = null!;
    private McpQuestionRegistry _registry = null!;
    private NotifySubscription _phone = null!;
    private NotifySubscription _laptop = null!;

    [TestInitialize]
    public void Init()
    {
        var options = new AgUiOptions();
        options.AllowedThreadIds.AddRange([Phone, Laptop]);
        _hub = new NotifyHub(options, _clock, epoch: "e1");
        _registry = new McpQuestionRegistry(options, _hub, _clock);
        _phone = _hub.Subscribe(Phone, "e1-0")!;
        _laptop = _hub.Subscribe(Laptop, "e1-0")!;
    }

    [TestCleanup]
    public void Cleanup()
    {
        _phone.Dispose();
        _laptop.Dispose();
    }

    /// <summary>The ids of the questions announced on a thread's notify stream since the last call.</summary>
    private static string[] Announced(NotifySubscription subscription)
    {
        var ids = new List<string>();
        while (subscription.Reader.TryRead(out var frame))
        {
            var e = JsonDocument.Parse(frame.Json).RootElement;
            Assert.AreEqual(TurnEventMapper.CustomNames.McpQuestion, Sse.Str(e, "name"));
            ids.Add(e.GetProperty("value").GetProperty("form").GetProperty("questionId").GetString()!);
        }
        return [.. ids];
    }

    private AgentReply FormReply(string questionId, string sessionId = Phone, TimeSpan? expiresIn = null) => new()
    {
        Content = "A question", SessionId = sessionId, AgentName = "mcp-bridge",
        ContentType = McpQuestionForm.ContentType,
        StructuredData = JsonSerializer.Serialize(new McpQuestionForm
        {
            QuestionId = questionId, ServerName = "scheduler", ToolName = "create_event", Message = "Which time zone?",
            ExpiresAt = _clock.Now + (expiresIn ?? TimeSpan.FromMinutes(10)),
            Fields = [new McpQuestionField { Name = "tz", Kind = McpQuestionFieldKind.String }],
        }, TurnEventMapper.PayloadJson),
    };

    private static McpQuestionStatus Status(string questionId, string state, string sessionId = Phone) =>
        new() { SessionId = sessionId, QuestionId = questionId, State = state };

    private static AgentReply FollowUp(string questionId, string sessionId = Phone) => new()
    {
        Content = "Booked.", SessionId = sessionId, AgentName = "RockBot", FollowsQuestionId = questionId,
    };

    private string[] OpenIds(string thread = Phone) => [.. _registry.Open(thread).Select(q => q.Form.QuestionId)];

    private static string[] Ids(IEnumerable<OpenQuestion> questions) => [.. questions.Select(q => q.Form.QuestionId)];

    /// <summary>A run's emitter, and the events it emitted.</summary>
    private static (TurnEventEmitter Emitter, List<BaseEvent> Events) Emitter(string thread = Phone)
    {
        var events = new List<BaseEvent>();
        return (new TurnEventEmitter(thread, "run-1", events.Add), events);
    }

    private RunQuestions Begin(QuestionMode mode, string thread = Phone, TurnEventEmitter? emitter = null)
    {
        var run = _registry.TryBeginRun(thread, mode, emitter ?? Emitter(thread).Emitter, out var blockedBy);
        Assert.IsNotNull(run, $"blocked by {string.Join(", ", Ids(blockedBy))}");
        return run;
    }

    // ── Open questions ───────────────────────────────────────────────────────

    [TestMethod]
    public void AForm_OpensAQuestion_OnItsThread()
    {
        _registry.OnForm(FormReply("q1"));
        _registry.OnForm(FormReply("q2", $"session/{Laptop}"));

        CollectionAssert.AreEqual(new[] { "q1" }, OpenIds(Phone));
        CollectionAssert.AreEqual(new[] { "q2" }, OpenIds(Laptop));
        Assert.AreEqual(Phone, _registry.Find("q1")?.ThreadId);
        Assert.IsNull(_registry.Find("q9"));
    }

    [TestMethod]
    public void AFormForSomeoneElsesSession_IsNotTracked()
    {
        _registry.OnForm(FormReply("q1", "blazor-session"));

        Assert.IsNull(_registry.Find("q1"));
        Assert.AreEqual(0, Announced(_phone).Length);
        Assert.AreEqual(0, Announced(_laptop).Length);
    }

    [TestMethod]
    [DataRow(McpQuestionState.Answered)]
    [DataRow(McpQuestionState.Declined)]
    [DataRow(McpQuestionState.Expired)]
    [DataRow(McpQuestionState.Cancelled)]
    [DataRow(McpQuestionState.Interrupted)]
    [DataRow(McpQuestionState.Unavailable)]
    [DataRow("a-state-from-a-newer-agent")]
    public void AClosingStatus_SettlesTheQuestion(string state)
    {
        _registry.OnForm(FormReply("q1"));
        _registry.OnForm(FormReply("q2"));

        _registry.OnStatus(Status("q1", state));

        CollectionAssert.AreEqual(new[] { "q2" }, OpenIds());
    }

    [TestMethod]
    public void Rejected_LeavesTheQuestionOpen()
    {
        _registry.OnForm(FormReply("q1"));

        _registry.OnStatus(Status("q1", McpQuestionState.Rejected));

        CollectionAssert.AreEqual(new[] { "q1" }, OpenIds());
    }

    [TestMethod]
    public void AStatusForAnUnknownQuestion_IsIgnored()
    {
        _registry.OnForm(FormReply("q1"));

        _registry.OnStatus(Status("q9", McpQuestionState.Answered));

        CollectionAssert.AreEqual(new[] { "q1" }, OpenIds());
    }

    [TestMethod]
    public void AQuestion_IsForgottenWhenItExpires_WithoutAStatus()
    {
        _registry.OnForm(FormReply("soon", expiresIn: TimeSpan.FromMinutes(1)));
        _registry.OnForm(FormReply("later", expiresIn: TimeSpan.FromMinutes(5)));

        _clock.Now += TimeSpan.FromSeconds(59);
        CollectionAssert.AreEqual(new[] { "soon", "later" }, OpenIds());

        _clock.Now += TimeSpan.FromSeconds(1);
        CollectionAssert.AreEqual(new[] { "later" }, OpenIds());
        Assert.IsNull(_registry.Find("soon"));

        // And it no longer blocks or has to be resumed.
        _clock.Now += TimeSpan.FromMinutes(5);
        Begin(QuestionMode.Interrupt);
    }

    // ── Where a form goes ────────────────────────────────────────────────────

    [TestMethod]
    public void WithNoRunOpen_AFormGoesToTheNotifyStream()
    {
        _registry.OnForm(FormReply("q1"));

        CollectionAssert.AreEqual(new[] { "q1" }, Announced(_phone));
        Assert.AreEqual(0, Announced(_laptop).Length);
    }

    [TestMethod]
    public void InACustomRun_AFormIsAnEventOfTheRun_AndNotOnNotify()
    {
        var (emitter, events) = Emitter();
        emitter.Start();
        var run = Begin(QuestionMode.Custom, emitter: emitter);

        _registry.OnForm(FormReply("q1"));

        var question = (CustomEvent)events[^1];
        Assert.AreEqual(TurnEventMapper.CustomNames.McpQuestion, question.Name);
        Assert.AreEqual("q1", question.Value!.Value.GetProperty("interrupt").GetProperty("id").GetString());
        Assert.AreEqual(0, Announced(_phone).Length);

        // The run ends with nothing to carry, and nothing is announced a second time.
        Assert.AreEqual(0, _registry.EndRun(run, carryInterrupts: true).Count);
        Assert.AreEqual(0, Announced(_phone).Length);
        CollectionAssert.AreEqual(new[] { "q1" }, OpenIds(), "still open: only the agent settles a question");
    }

    [TestMethod]
    public void InAnInterruptRun_AFormIsHeld_AndEndsTheRunAsAnInterrupt()
    {
        var (emitter, events) = Emitter();
        emitter.Start();
        var run = Begin(QuestionMode.Interrupt, emitter: emitter);

        _registry.OnForm(FormReply("q1"));
        _registry.OnForm(FormReply("q2"));

        Assert.AreEqual(1, events.Count, "nothing in the stream but RUN_STARTED");
        Assert.AreEqual(0, Announced(_phone).Length);

        CollectionAssert.AreEqual(new[] { "q1", "q2" }, Ids(_registry.EndRun(run, carryInterrupts: true)));
        Assert.AreEqual(0, Announced(_phone).Length, "the run's RUN_FINISHED carries them");
    }

    [TestMethod]
    public void AnInterruptRunThatCannotCarryThem_HandsItsQuestionsToNotify()
    {
        var run = Begin(QuestionMode.Interrupt);
        _registry.OnForm(FormReply("q1"));

        // The run failed or was cancelled: no RUN_FINISHED to put interrupts in.
        Assert.AreEqual(0, _registry.EndRun(run, carryInterrupts: false).Count);

        CollectionAssert.AreEqual(new[] { "q1" }, Announced(_phone));
        CollectionAssert.AreEqual(new[] { "q1" }, OpenIds());
    }

    [TestMethod]
    public void ARunWithoutForms_LeavesAFormToNotify()
    {
        var (emitter, events) = Emitter();
        emitter.Start();
        var run = Begin(QuestionMode.None, emitter: emitter);

        _registry.OnForm(FormReply("q1"));

        Assert.AreEqual(1, events.Count);
        CollectionAssert.AreEqual(new[] { "q1" }, Announced(_phone));
        Assert.AreEqual(0, _registry.EndRun(run, carryInterrupts: true).Count, "a run without forms carries no interrupts");
    }

    [TestMethod]
    public void AFormOnAnotherThread_IsNotTheRunsBusiness()
    {
        var run = Begin(QuestionMode.Interrupt, Phone);

        _registry.OnForm(FormReply("q1", Laptop));

        CollectionAssert.AreEqual(new[] { "q1" }, Announced(_laptop));
        Assert.AreEqual(0, _registry.EndRun(run, carryInterrupts: true).Count);
    }

    [TestMethod]
    public void AFormAfterTheRunEnded_GoesToNotify()
    {
        var run = Begin(QuestionMode.Interrupt);
        _registry.EndRun(run, carryInterrupts: true);

        _registry.OnForm(FormReply("q1"));

        CollectionAssert.AreEqual(new[] { "q1" }, Announced(_phone));
    }

    [TestMethod]
    public void EndingARunTwice_DoesNothingTheSecondTime()
    {
        var run = Begin(QuestionMode.Interrupt);
        _registry.OnForm(FormReply("q1"));

        Assert.AreEqual(1, _registry.EndRun(run, carryInterrupts: true).Count);
        Assert.AreEqual(0, _registry.EndRun(run, carryInterrupts: false).Count);
        Assert.AreEqual(0, Announced(_phone).Length, "the second call must not announce what the first carried");
    }

    [TestMethod]
    public void WithTwoInterruptRuns_TheHeldQuestionWaitsForOneThatCanCarryIt()
    {
        var first = Begin(QuestionMode.Interrupt);
        var second = Begin(QuestionMode.Interrupt);
        _registry.OnForm(FormReply("q1"));

        _registry.EndRun(first, carryInterrupts: false);
        Assert.AreEqual(0, Announced(_phone).Length, "the other run still holds it");

        CollectionAssert.AreEqual(new[] { "q1" }, Ids(_registry.EndRun(second, carryInterrupts: true)));
    }

    // ── Blocking ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void AnInterruptRun_IsBlockedWhileQuestionsAreOpen()
    {
        _registry.OnForm(FormReply("q1"));
        _registry.OnForm(FormReply("q2"));

        var run = _registry.TryBeginRun(Phone, QuestionMode.Interrupt, Emitter().Emitter, out var blockedBy);

        Assert.IsNull(run);
        CollectionAssert.AreEqual(new[] { "q1", "q2" }, Ids(blockedBy));
    }

    [TestMethod]
    [DataRow(nameof(QuestionMode.Custom))]
    [DataRow(nameof(QuestionMode.None))]
    public void OtherRuns_AreNeverBlocked(string mode)
    {
        _registry.OnForm(FormReply("q1"));

        Begin(Enum.Parse<QuestionMode>(mode));
    }

    [TestMethod]
    public void AnotherThreadsQuestions_DoNotBlock()
    {
        _registry.OnForm(FormReply("q1", Laptop));

        Begin(QuestionMode.Interrupt, Phone);
    }

    [TestMethod]
    public void OnceSettled_AQuestionNoLongerBlocks()
    {
        _registry.OnForm(FormReply("q1"));
        _registry.OnStatus(Status("q1", McpQuestionState.Answered));

        Begin(QuestionMode.Interrupt);
    }

    // ── Resuming ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void AResume_MustAnswerEveryOpenQuestion()
    {
        _registry.OnForm(FormReply("q1"));
        _registry.OnForm(FormReply("q2"));

        var start = _registry.TryBeginResume(Phone, ["q2"], Emitter().Emitter);

        Assert.IsNull(start.Run);
        CollectionAssert.AreEqual(new[] { "q1" }, Ids(start.Unanswered));
        CollectionAssert.AreEqual(new[] { "q1", "q2" }, Ids(start.AllOpen));
    }

    [TestMethod]
    public void AResumeOfEverything_GoesAhead_AndHearsTheStatusAndTheReply()
    {
        _registry.OnForm(FormReply("q1"));
        _registry.OnForm(FormReply("q2"));

        var run = _registry.TryBeginResume(Phone, ["q1", "q2"], Emitter().Emitter).Run;
        Assert.IsNotNull(run);

        _registry.OnStatus(Status("q1", McpQuestionState.Answered));
        Assert.IsTrue(_registry.TryRouteFollowUp(FollowUp("q1")));

        Assert.IsTrue(run.Signals.TryRead(out var first));
        Assert.AreEqual(McpQuestionState.Answered, ((QuestionStatusSignal)first).Status.State);
        Assert.IsTrue(run.Signals.TryRead(out var second));
        Assert.AreEqual("q1", ((FollowUpSignal)second).Reply.FollowsQuestionId);
        Assert.IsFalse(run.Signals.TryRead(out _), "q2 has said nothing yet");
    }

    [TestMethod]
    public void AResume_MayNameAQuestionTheProxyDoesNotKnow()
    {
        // After a restart the proxy has no questions; the agent is the one that knows.
        var start = _registry.TryBeginResume(Phone, ["from-before-the-restart"], Emitter().Emitter);

        Assert.IsNotNull(start.Run);
        _registry.OnStatus(Status("from-before-the-restart", McpQuestionState.Unavailable));
        Assert.IsTrue(start.Run.Signals.TryRead(out _));
    }

    [TestMethod]
    public void TwoRuns_CannotResumeTheSameQuestion()
    {
        _registry.OnForm(FormReply("q1"));
        var first = _registry.TryBeginResume(Phone, ["q1"], Emitter().Emitter);
        Assert.IsNotNull(first.Run);

        var second = _registry.TryBeginResume(Phone, ["q1"], Emitter().Emitter);

        Assert.IsNull(second.Run);
        Assert.AreEqual("q1", second.BusyQuestionId);

        // Once the first is over, the question can be resumed again (it was rejected, say).
        _registry.EndRun(first.Run, carryInterrupts: false);
        Assert.IsNotNull(_registry.TryBeginResume(Phone, ["q1"], Emitter().Emitter).Run);
    }

    [TestMethod]
    public void AQuestionAskedDuringAResume_EndsThatRunAsAnInterrupt()
    {
        _registry.OnForm(FormReply("q1"));
        Announced(_phone);
        var run = _registry.TryBeginResume(Phone, ["q1"], Emitter().Emitter).Run!;

        // The server takes the answer and asks something else.
        _registry.OnStatus(Status("q1", McpQuestionState.Answered));
        _registry.OnForm(FormReply("q2"));

        Assert.AreEqual(0, Announced(_phone).Length);
        CollectionAssert.AreEqual(new[] { "q2" }, Ids(_registry.EndRun(run, carryInterrupts: true)));
    }

    // ── The reply to an answer ───────────────────────────────────────────────

    [TestMethod]
    public void AFollowUp_NobodyResumed_IsNotTaken()
    {
        _registry.OnForm(FormReply("q1"));

        Assert.IsFalse(_registry.TryRouteFollowUp(FollowUp("q1")));
        Assert.IsFalse(_registry.TryRouteFollowUp(new AgentReply { Content = "Hello", SessionId = Phone, AgentName = "RockBot" }));
    }

    [TestMethod]
    public void AFollowUp_IsTakenOnce()
    {
        var run = _registry.TryBeginResume(Phone, ["q1"], Emitter().Emitter).Run!;

        Assert.IsTrue(_registry.TryRouteFollowUp(FollowUp("q1")));
        Assert.IsFalse(_registry.TryRouteFollowUp(FollowUp("q1")), "a second reply for the same question is unsolicited");

        Assert.IsTrue(run.Signals.TryRead(out _));
        Assert.IsFalse(run.Signals.TryRead(out _));
    }

    [TestMethod]
    public void OnceTheRunStopsWaiting_TheFollowUpIsNotTaken()
    {
        var run = _registry.TryBeginResume(Phone, ["q1", "q2"], Emitter().Emitter).Run!;

        _registry.StopWaiting(run, "q1");

        Assert.IsFalse(_registry.TryRouteFollowUp(FollowUp("q1")));
        Assert.IsTrue(_registry.TryRouteFollowUp(FollowUp("q2")), "it still waits for the other");
    }

    [TestMethod]
    public void OnceTheRunEnded_TheFollowUpIsNotTaken_AndItsSignalsAreComplete()
    {
        var run = _registry.TryBeginResume(Phone, ["q1"], Emitter().Emitter).Run!;

        _registry.EndRun(run, carryInterrupts: true);

        Assert.IsFalse(_registry.TryRouteFollowUp(FollowUp("q1")));
        Assert.IsTrue(run.Signals.Completion.IsCompleted);
    }

    [TestMethod]
    public void StopWaiting_OnlyReleasesTheRunsOwnClaim()
    {
        var owner = _registry.TryBeginResume(Phone, ["q1"], Emitter().Emitter).Run!;
        var other = _registry.TryBeginResume(Phone, ["q2"], Emitter().Emitter).Run!;

        _registry.StopWaiting(other, "q1");

        Assert.IsTrue(_registry.TryRouteFollowUp(FollowUp("q1")));
        Assert.IsTrue(owner.Signals.TryRead(out _));
    }
}

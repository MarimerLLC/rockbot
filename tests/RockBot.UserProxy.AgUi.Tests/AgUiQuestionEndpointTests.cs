using System.Net;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// MCP questions over the real pipeline, in both modes (#794, phase 3). The fake agent behaves as
/// the real one does: the form, its statuses and the reply to an answer are all broadcast and
/// uncorrelated, and the form arrives while the turn that caused it is still running.
/// </summary>
[TestClass]
public sealed class AgUiQuestionEndpointTests
{
    private const string Thread = "agui-session";
    private const string InterruptMode = ""","forwardedProps":{"rockbot":{"questionMode":"interrupt"}}""";
    private const string CustomMode = ""","forwardedProps":{"rockbot":{"questionMode":"custom"}}""";
    private static readonly TimeSpan Patience = NotifyStream.Patience;

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private readonly ManualClock _clock = new();
    private AgUiTestHost _host = null!;
    private HttpClient _client = null!;
    private FakeAgent _agent = null!;

    [TestInitialize]
    public Task Init() => StartAsync();

    [TestCleanup]
    public async Task Cleanup()
    {
        await _agent.DisposeAsync();
        _client.Dispose();
        await _host.DisposeAsync();
    }

    private async Task StartAsync(params (string Key, string Value)[] settings)
    {
        // The registry reads question expiry from this clock; everything else keeps real time.
        _host = new AgUiTestHost(settings) { ConfigureServices = services => services.AddSingleton<TimeProvider>(_clock) };
        _client = _host.CreateClient();
        _agent = await FakeAgent.StartAsync(_host);
    }

    private async Task RestartAsync(params (string Key, string Value)[] settings)
    {
        await Cleanup();
        await StartAsync(settings);
    }

    private McpQuestionRegistry Registry => _host.Services.GetRequiredService<McpQuestionRegistry>();

    private McpQuestionForm Form(string questionId = "q1", TimeSpan? expiresIn = null, params McpQuestionField[] fields) => new()
    {
        QuestionId = questionId, ServerName = "scheduler", ToolName = "create_event", Message = "Which time zone?",
        ExpiresAt = _clock.GetUtcNow() + (expiresIn ?? TimeSpan.FromMinutes(10)),
        Fields = fields.Length > 0
            ? fields
            : [new McpQuestionField { Name = "tz", Kind = McpQuestionFieldKind.String, Title = "Time zone", Required = true }],
    };

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static StringContent Run(string runId, string more = "", string text = "Book it") =>
        Json($$"""{"threadId":"agui-session","runId":"{{runId}}","messages":[{"id":"m1","role":"user","content":"{{text}}"}]{{more}}}""");

    /// <summary>A run that resumes. It replays a message, as a real client does; the proxy must not take it for new input.</summary>
    private static StringContent Resume(string runId, string entries) =>
        Json($$"""{"threadId":"agui-session","runId":"{{runId}}","messages":[{"id":"m1","role":"user","content":"Book it"}],"resume":[{{entries}}]}""");

    private static string Resolved(string questionId, string payload = """{"tz":"Europe/Brussels"}""") =>
        $$"""{"interruptId":"{{questionId}}","status":"resolved","payload":{{payload}}}""";

    private static string Cancelled(string questionId) => $$"""{"interruptId":"{{questionId}}","status":"cancelled"}""";

    private async Task<List<JsonElement>> RunToEndAsync(HttpContent content)
    {
        using var response = await _client.PostAsync("/agui", content).WaitAsync(Patience);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return Sse.Events(await response.Content.ReadAsStringAsync().WaitAsync(Patience));
    }

    private static string[] Types(IEnumerable<JsonElement> events) => [.. events.Select(Sse.Type)];

    /// <summary>Each event as its type, or its name for a <c>CUSTOM</c> one.</summary>
    private static string[] Shape(IEnumerable<JsonElement> events) =>
        [.. events.Select(e => Sse.Type(e) == "CUSTOM" ? Sse.Str(e, "name")! : Sse.Type(e))];

    private static string? Code(List<JsonElement> events) => Sse.Str(events[^1], "code");

    /// <summary>The interrupts a run ended with; fails if it ended any other way.</summary>
    private static List<JsonElement> Interrupts(List<JsonElement> events)
    {
        Assert.AreEqual("RUN_FINISHED", Sse.Type(events[^1]));
        var outcome = events[^1].GetProperty("outcome");
        Assert.AreEqual("interrupt", outcome.GetProperty("type").GetString());
        return [.. outcome.GetProperty("interrupts").EnumerateArray()];
    }

    private static void AssertPlainFinish(List<JsonElement> events)
    {
        Assert.AreEqual("RUN_FINISHED", Sse.Type(events[^1]));
        Assert.IsFalse(events[^1].TryGetProperty("outcome", out _), "an ordinary success has no outcome");
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, $"timed out waiting until {what}");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Shows a form and waits until the proxy has it. The form and a turn's reply travel on
    /// different subscriptions, so a test that needs "the form, then the reply" has to wait.
    /// </summary>
    private async Task ShowFormAsync(McpQuestionForm form)
    {
        await _agent.FormAsync(form);
        await UntilAsync(() => Registry.Find(form.QuestionId) is not null, $"the proxy has question {form.QuestionId}");
    }

    /// <summary>An agent whose turn hands back a question as a form, then says so, as the real one does.</summary>
    private void AgentAsksDuringTheTurn(params McpQuestionForm[] forms) => _agent.OnMessage = async (agent, received) =>
    {
        foreach (var form in forms)
            await ShowFormAsync(form);
        await agent.ReplyAsync(received, "Please fill in the form.");
    };

    /// <summary>
    /// Everything on the notify stream up to now, as <see cref="Shape"/> names. A marker is
    /// broadcast and the stream read up to it, so "nothing else arrived" can be asserted.
    /// </summary>
    private async Task<List<NotifyStream.Frame>> NotifiedAsync(NotifyStream stream)
    {
        await _agent.BroadcastAsync(new AgentNameChanged { AgentName = "marker" });
        var frames = new List<NotifyStream.Frame>();
        while (true)
        {
            var frame = await stream.ReadAsync();
            if (frame.Name == TurnEventMapper.CustomNames.AgentName)
                return frames;
            frames.Add(frame);
        }
    }

    private static string[] Shape(IEnumerable<NotifyStream.Frame> frames) => Shape(frames.Select(f => f.Event));

    // ── Interrupt mode: a question during a run ──────────────────────────────

    [TestMethod]
    public async Task Interrupt_AQuestionDuringTheRun_EndsItWithAnInterrupt()
    {
        using var notify = await NotifyStream.OpenAsync(_client);
        await notify.ReadConnectedAsync();
        var form = Form();
        AgentAsksDuringTheTurn(form);

        var events = await RunToEndAsync(Run("run-1", InterruptMode));

        // Held, not shown: the run's own stream has only the reply, then the interrupt.
        CollectionAssert.AreEqual(
            new[] { "RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED" },
            Types(events));

        var interrupt = Interrupts(events).Single();
        Assert.AreEqual("q1", interrupt.GetProperty("id").GetString());
        Assert.AreEqual("input_required", interrupt.GetProperty("reason").GetString());
        Assert.AreEqual("Which time zone?", interrupt.GetProperty("message").GetString());
        // ISO 8601, to the millisecond.
        var expiresAt = DateTimeOffset.Parse(interrupt.GetProperty("expiresAt").GetString()!);
        Assert.IsTrue((form.ExpiresAt - expiresAt).Duration() < TimeSpan.FromMilliseconds(1), $"{expiresAt:O} is not {form.ExpiresAt:O}");
        var schema = interrupt.GetProperty("responseSchema");
        Assert.AreEqual("string", schema.GetProperty("properties").GetProperty("tz").GetProperty("type").GetString());
        Assert.AreEqual("tz", schema.GetProperty("required")[0].GetString());

        // The SDK reads the terminal event back as an interrupt outcome.
        var parsed = (RunFinishedEvent)JsonSerializer.Deserialize(events[^1].GetRawText(), AGUIJsonSerializerContext.Default.BaseEvent)!;
        Assert.AreEqual("q1", ((RunFinishedInterruptOutcome)parsed.Outcome!).Interrupts.Single().Id);

        // The run asked for forms, so the agent knows it may send them.
        Assert.IsTrue(_agent.Messages.Single().Message.ClientCapabilities.HasFlag(ClientCapabilities.InteractiveForms));
        // Delivered once: the run carried it, so the notify stream doesn't.
        Assert.AreEqual(0, (await NotifiedAsync(notify)).Count);
    }

    [TestMethod]
    public async Task Interrupt_IsTheDefaultMode_ForAClientThatDeclaresForms()
    {
        AgentAsksDuringTheTurn(Form());

        var events = await RunToEndAsync(Run("run-1", ""","forwardedProps":{"rockbot":{"capabilities":["MarkdownBasic","InteractiveForms"]}}"""));

        Assert.AreEqual("q1", Interrupts(events).Single().GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task Interrupt_ASingleDecision_IsAConfirmation_AndSeveralQuestionsAreSeveralInterrupts()
    {
        AgentAsksDuringTheTurn(
            Form("approve", fields: new McpQuestionField { Name = "ok", Kind = McpQuestionFieldKind.Boolean, IsDecision = true }),
            Form("details"));

        var interrupts = Interrupts(await RunToEndAsync(Run("run-1", InterruptMode)));

        CollectionAssert.AreEqual(new[] { "approve", "details" }, interrupts.Select(i => i.GetProperty("id").GetString()).ToArray());
        Assert.AreEqual("confirmation", interrupts[0].GetProperty("reason").GetString());
        Assert.AreEqual("input_required", interrupts[1].GetProperty("reason").GetString());
    }

    [TestMethod]
    public async Task Interrupt_ARunThatFails_HandsItsQuestionToTheNotifyStream()
    {
        using var notify = await NotifyStream.OpenAsync(_client);
        await notify.ReadConnectedAsync();
        _agent.OnMessage = async (agent, received) =>
        {
            await agent.RunStartedAsync(received);
            await ShowFormAsync(Form());
            await agent.RunFinishedAsync(received, RunOutcome.Error, "the model refused");
            await agent.ReplyAsync(received, "Sorry, that failed.");
        };

        var events = await RunToEndAsync(Run("run-1", InterruptMode));

        // RUN_ERROR has no place for interrupts, and the question is still open.
        Assert.AreEqual("RUN_ERROR", Sse.Type(events[^1]));
        var question = (await NotifiedAsync(notify)).Single();
        Assert.AreEqual(TurnEventMapper.CustomNames.McpQuestion, question.Name);
        Assert.AreEqual("q1", question.Value.GetProperty("interrupt").GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task WithoutForms_ARunNeverEndsWithAnInterrupt_AndIsNeverBlocked()
    {
        using var notify = await NotifyStream.OpenAsync(_client);
        await notify.ReadConnectedAsync();
        // Another client's question is open on the thread.
        await ShowFormAsync(Form("theirs"));
        AgentAsksDuringTheTurn(Form("q1"));

        // No forwardedProps at all: a generic client that never said it handles forms.
        var events = await RunToEndAsync(Run("run-1"));

        AssertPlainFinish(events);
        Assert.IsFalse(_agent.Messages.Single().Message.ClientCapabilities.HasFlag(ClientCapabilities.InteractiveForms));
        // Both forms are on the notify stream, for whichever client does show them.
        CollectionAssert.AreEqual(
            new[] { "theirs", "q1" },
            (await NotifiedAsync(notify)).Select(f => f.Value.GetProperty("form").GetProperty("questionId").GetString()).ToArray());
    }

    // ── Interrupt mode: a question outside a run, blocking, resuming ─────────

    [TestMethod]
    public async Task Interrupt_AQuestionOutsideARun_GoesToNotify_BlocksNewInput_AndIsResumed()
    {
        using var notify = await NotifyStream.OpenAsync(_client);
        await notify.ReadConnectedAsync();

        // No run is open: the question is announced on the notify stream, as an interrupt too.
        await _agent.FormAsync(Form());
        var question = await notify.ReadAsync();
        Assert.AreEqual(TurnEventMapper.CustomNames.McpQuestion, question.Name);
        Assert.AreEqual("q1", question.Value.GetProperty("form").GetProperty("questionId").GetString());
        Assert.AreEqual("q1", question.Value.GetProperty("interrupt").GetProperty("id").GetString());
        Assert.IsTrue(question.Value.GetProperty("interrupt").TryGetProperty("responseSchema", out _));

        // New input is refused in the stream, with the question again so the client can answer it.
        var blocked = await RunToEndAsync(Run("run-1", InterruptMode));
        CollectionAssert.AreEqual(new[] { "RUN_STARTED", TurnEventMapper.CustomNames.McpQuestion, "RUN_ERROR" }, Shape(blocked));
        Assert.AreEqual(TurnEventMapper.ErrorCodes.InterruptsPending, Code(blocked));
        StringAssert.Contains(Sse.Str(blocked[^1], "message"), "q1");
        Assert.AreEqual("q1", blocked[1].GetProperty("value").GetProperty("interrupt").GetProperty("id").GetString());
        Assert.AreEqual(0, _agent.Messages.Count, "blocked input never reaches the agent");

        // The resume answers it: the agent gets the payload as the form's answers.
        _agent.OnAnswer = (agent, answer) => agent.AcceptAsync(answer, "Booked for Brussels time.");
        var resumed = await RunToEndAsync(Resume("run-2", Resolved("q1")));

        var answer = _agent.Answers.Single();
        Assert.AreEqual("q1", answer.QuestionId);
        Assert.AreEqual(Thread, answer.SessionId);
        Assert.AreEqual("agui-user", answer.UserId);
        Assert.IsFalse(answer.Decline);
        Assert.AreEqual("Europe/Brussels", JsonDocument.Parse(answer.Answers!).RootElement.GetProperty("tz").GetString());
        Assert.AreEqual(0, _agent.Messages.Count, "a resume sends no user message, whatever it replays");

        // The run streams the status and the reply that follows the answer, then ends normally.
        CollectionAssert.AreEqual(
            new[]
            {
                "RUN_STARTED", TurnEventMapper.CustomNames.McpQuestionStatus,
                "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", TurnEventMapper.CustomNames.Origin,
                "RUN_FINISHED",
            },
            Shape(resumed));
        Assert.AreEqual("run-2", Sse.Str(resumed[0], "runId"));
        Assert.AreEqual("answered", resumed[1].GetProperty("value").GetProperty("state").GetString());
        Assert.AreEqual("Booked for Brussels time.", Sse.Str(resumed[3], "delta"));
        Assert.AreEqual("q1", resumed[5].GetProperty("value").GetProperty("followsQuestionId").GetString());
        AssertPlainFinish(resumed);

        // The notify stream has the status (every client closes the form) and not the reply.
        CollectionAssert.AreEqual(new[] { TurnEventMapper.CustomNames.McpQuestionStatus }, Shape(await NotifiedAsync(notify)));

        // And the thread takes new input again.
        _agent.OnMessage = (agent, received) => agent.ReplyAsync(received, "Anything else?");
        AssertPlainFinish(await RunToEndAsync(Run("run-3", InterruptMode)));
        Assert.AreEqual(1, _agent.Messages.Count);
    }

    [TestMethod]
    public async Task Resume_Cancelled_DeclinesTheQuestion()
    {
        await ShowFormAsync(Form());
        _agent.OnAnswer = (agent, answer) => agent.AcceptAsync(answer, "Fine, I left it unbooked.");

        var events = await RunToEndAsync(Resume("run-1", Cancelled("q1")));

        var answer = _agent.Answers.Single();
        Assert.IsTrue(answer.Decline);
        Assert.IsNull(answer.Answers);
        Assert.AreEqual("declined", events[1].GetProperty("value").GetProperty("state").GetString());
        Assert.AreEqual("Fine, I left it unbooked.", Sse.Str(events.Single(e => Sse.Type(e) == "TEXT_MESSAGE_CONTENT"), "delta"));
        AssertPlainFinish(events);
        Assert.AreEqual(0, Registry.Open(Thread).Count);
    }

    [TestMethod]
    public async Task Resume_AnswersSeveralInterrupts_AndWaitsForEachReply()
    {
        await ShowFormAsync(Form("q1"));
        await ShowFormAsync(Form("q2"));
        _agent.OnAnswer = (agent, answer) => agent.AcceptAsync(answer, $"Done with {answer.QuestionId}.");

        var events = await RunToEndAsync(Resume("run-1", $"{Resolved("q1")},{Cancelled("q2")}"));

        CollectionAssert.AreEquivalent(new[] { "q1", "q2" }, _agent.Answers.Select(a => a.QuestionId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "Done with q1.", "Done with q2." },
            events.Where(e => Sse.Type(e) == "TEXT_MESSAGE_CONTENT").Select(e => Sse.Str(e, "delta")).ToArray());
        AssertPlainFinish(events);
    }

    [TestMethod]
    public async Task Resume_ThatMissesAnOpenInterrupt_IsRunError_AndAnswersNothing()
    {
        await ShowFormAsync(Form("q1"));
        await ShowFormAsync(Form("q2"));

        var events = await RunToEndAsync(Resume("run-1", Resolved("q2")));

        // Both open questions, so the client sees what it has to answer; then the error.
        CollectionAssert.AreEqual(
            new[] { "RUN_STARTED", TurnEventMapper.CustomNames.McpQuestion, TurnEventMapper.CustomNames.McpQuestion, "RUN_ERROR" },
            Shape(events));
        Assert.AreEqual(TurnEventMapper.ErrorCodes.ResumeIncomplete, Code(events));
        StringAssert.Contains(Sse.Str(events[^1], "message"), "q1");
        Assert.AreEqual(0, _agent.Answers.Count, "a partial resume must not answer the interrupts it did name");
        Assert.AreEqual(2, Registry.Open(Thread).Count);
    }

    [TestMethod]
    public async Task Resume_Rejected_IsRunError_AndTheInterruptCanBeResumedAgain()
    {
        await ShowFormAsync(Form());
        _agent.OnAnswer = (agent, answer) => answer.Answers!.Contains("Mars")
            ? agent.StatusAsync(answer.QuestionId, McpQuestionState.Rejected, reason: "tz is not a time zone.")
            : agent.AcceptAsync(answer, "Booked.");

        var rejected = await RunToEndAsync(Resume("run-1", Resolved("q1", """{"tz":"Mars"}""")));

        CollectionAssert.AreEqual(new[] { "RUN_STARTED", TurnEventMapper.CustomNames.McpQuestionStatus, "RUN_ERROR" }, Shape(rejected));
        Assert.AreEqual(TurnEventMapper.ErrorCodes.InterruptRejected, Code(rejected));
        StringAssert.Contains(Sse.Str(rejected[^1], "message"), "tz is not a time zone.");
        Assert.AreEqual(1, Registry.Open(Thread).Count, "a rejected question is still open");

        // Still open, so still blocking, and still resumable.
        Assert.AreEqual(TurnEventMapper.ErrorCodes.InterruptsPending, Code(await RunToEndAsync(Run("run-2", InterruptMode))));

        var accepted = await RunToEndAsync(Resume("run-3", Resolved("q1")));
        Assert.AreEqual("Booked.", Sse.Str(accepted.Single(e => Sse.Type(e) == "TEXT_MESSAGE_CONTENT"), "delta"));
        AssertPlainFinish(accepted);
        Assert.AreEqual(2, _agent.Answers.Count);
    }

    [TestMethod]
    public async Task Resume_WhenTheServerAsksAgain_EndsWithTheNewInterrupt()
    {
        await ShowFormAsync(Form("q1"));
        _agent.OnAnswer = async (agent, answer) =>
        {
            // The bridge accepts the answer, the server asks something else, then the agent replies.
            await agent.StatusAsync(answer.QuestionId, McpQuestionState.Answered, answeredBy: "user");
            await agent.FormAsync(Form("q2"));
            await agent.FollowUpAsync(answer.QuestionId, "One more thing: please fill in the next form.");
        };

        var events = await RunToEndAsync(Resume("run-1", Resolved("q1")));

        Assert.AreEqual("q2", Interrupts(events).Single().GetProperty("id").GetString());
        Assert.IsFalse(Shape(events).Contains(TurnEventMapper.CustomNames.McpQuestion), "held for RUN_FINISHED, not shown twice");
        CollectionAssert.AreEqual(new[] { "q2" }, Registry.Open(Thread).Select(q => q.Form.QuestionId).ToArray());
    }

    [TestMethod]
    public async Task Resume_OfAQuestionThatIsGone_EndsNormallyWithItsStatus()
    {
        // The proxy doesn't know q1 (it restarted, say). The agent is the one that can tell.
        _agent.OnAnswer = (agent, answer) =>
            agent.StatusAsync(answer.QuestionId, McpQuestionState.Unavailable, reason: "No such question.");

        var events = await RunToEndAsync(Resume("run-1", Resolved("q1")));

        CollectionAssert.AreEqual(new[] { "RUN_STARTED", TurnEventMapper.CustomNames.McpQuestionStatus, "RUN_FINISHED" }, Shape(events));
        Assert.AreEqual("unavailable", events[1].GetProperty("value").GetProperty("state").GetString());
        AssertPlainFinish(events);
        Assert.AreEqual("q1", _agent.Answers.Single().QuestionId);
    }

    [TestMethod]
    public async Task Resume_AnsweredByTheAgentItself_DoesNotWaitForAReply()
    {
        await ShowFormAsync(Form());
        // The agent's own mcp_answer got there first: no reply with FollowsQuestionId will come.
        _agent.OnAnswer = (agent, answer) => agent.StatusAsync(answer.QuestionId, McpQuestionState.Answered, answeredBy: "agent");

        var events = await RunToEndAsync(Resume("run-1", Resolved("q1")));

        CollectionAssert.AreEqual(new[] { "RUN_STARTED", TurnEventMapper.CustomNames.McpQuestionStatus, "RUN_FINISHED" }, Shape(events));
    }

    [TestMethod]
    public async Task Resume_WhenTheAgentNeverAcknowledges_IsRunError_AndTheInterruptStaysOpen()
    {
        await RestartAsync(("UserProxy:ReplyTimeout", "00:00:00.300"));
        await ShowFormAsync(Form());

        var events = await RunToEndAsync(Resume("run-1", Resolved("q1")));

        CollectionAssert.AreEqual(new[] { "RUN_STARTED", "RUN_ERROR" }, Types(events));
        Assert.AreEqual(TurnEventMapper.ErrorCodes.AgentLost, Code(events));
        Assert.AreEqual(1, Registry.Open(Thread).Count);
    }

    [TestMethod]
    public async Task Resume_WhenTheReplyTakesTooLong_EndsWithReplyPending_AndTheReplyArrivesOnNotify()
    {
        await RestartAsync(("AgUi:ResumeReplyTimeout", "00:00:00.300"));
        using var notify = await NotifyStream.OpenAsync(_client);
        await notify.ReadConnectedAsync();
        await ShowFormAsync(Form());
        _agent.OnAnswer = (agent, answer) => agent.StatusAsync(answer.QuestionId, McpQuestionState.Answered, answeredBy: "user");

        var events = await RunToEndAsync(Resume("run-1", Resolved("q1")));

        // Not an error: the answer was accepted, and the agent may still reply.
        CollectionAssert.AreEqual(
            new[] { "RUN_STARTED", TurnEventMapper.CustomNames.McpQuestionStatus, TurnEventMapper.CustomNames.ReplyPending, "RUN_FINISHED" },
            Shape(events));
        AssertPlainFinish(events);

        // The late reply isn't lost: the run gave up its claim, so it goes to the notify stream.
        await _agent.FollowUpAsync("q1", "Booked, eventually.");
        var notified = await NotifiedAsync(notify);
        CollectionAssert.AreEqual(
            new[]
            {
                TurnEventMapper.CustomNames.McpQuestion, TurnEventMapper.CustomNames.McpQuestionStatus,
                "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", TurnEventMapper.CustomNames.Origin,
            },
            Shape(notified));
        Assert.AreEqual("Booked, eventually.", Sse.Str(notified[3].Event, "delta"));
        Assert.AreEqual("q1", notified[5].Value.GetProperty("followsQuestionId").GetString());
    }

    [TestMethod]
    public async Task Resume_WhileAnotherRunResumesTheSameInterrupt_Is409()
    {
        await ShowFormAsync(Form());
        // The agent says nothing, so the first resume stays open.
        var first = RunToEndAsync(Resume("run-1", Resolved("q1")));
        await UntilAsync(() => _agent.Answers.Count == 1, "the first resume sent its answer");

        using var second = await _client.PostAsync("/agui", Resume("run-2", Resolved("q1")));

        Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode);
        Assert.AreEqual(1, _agent.Answers.Count);

        await _agent.AcceptAsync(_agent.Answers[0], "Booked.");
        AssertPlainFinish(await first.WaitAsync(Patience));
    }

    [TestMethod]
    public async Task Resume_WhenTheClientLeaves_CancelsNothing_AndTheReplyGoesToNotify()
    {
        using var notify = await NotifyStream.OpenAsync(_client);
        await notify.ReadConnectedAsync();
        await ShowFormAsync(Form());

        using var abort = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agui") { Content = Resume("run-1", Resolved("q1")) };
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        await UntilAsync(() => _agent.Answers.Count == 1, "the resume sent its answer");

        await abort.CancelAsync();
        response.Dispose();
        // The run is over once its id can be used again.
        await UntilAsync(() => _host.Services.GetRequiredService<AgUiRunRegistry>().TryStart("run-1"), "the aborted run ended");

        await _agent.AcceptAsync(_agent.Answers[0], "Booked.");

        var notified = await NotifiedAsync(notify);
        CollectionAssert.AreEqual(
            new[]
            {
                TurnEventMapper.CustomNames.McpQuestion, TurnEventMapper.CustomNames.McpQuestionStatus,
                "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", TurnEventMapper.CustomNames.Origin,
            },
            Shape(notified));
        // The answer was sent and can't be taken back; cancelling the session would stop the work it released.
        Assert.IsFalse(_agent.CancelRequested.IsCompleted);
    }

    // ── Custom mode ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Custom_AQuestionDuringTheRun_IsAnEventOfTheRun_AndTheRunEndsNormally()
    {
        using var notify = await NotifyStream.OpenAsync(_client);
        await notify.ReadConnectedAsync();
        AgentAsksDuringTheTurn(Form());

        var events = await RunToEndAsync(Run("run-1", CustomMode));

        CollectionAssert.AreEqual(
            new[]
            {
                "RUN_STARTED", TurnEventMapper.CustomNames.McpQuestion,
                "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED",
            },
            Shape(events));
        var value = events[1].GetProperty("value");
        Assert.AreEqual("q1", value.GetProperty("form").GetProperty("questionId").GetString());
        Assert.AreEqual("Time zone", value.GetProperty("form").GetProperty("fields")[0].GetProperty("title").GetString());
        Assert.AreEqual("scheduler asks: Which time zone?", value.GetProperty("text").GetString());
        Assert.AreEqual(Thread, value.GetProperty("sessionId").GetString());
        AssertPlainFinish(events);

        Assert.IsTrue(_agent.Messages.Single().Message.ClientCapabilities.HasFlag(ClientCapabilities.InteractiveForms));
        Assert.AreEqual(0, (await NotifiedAsync(notify)).Count, "shown in the run, so not on the notify stream as well");
    }

    [TestMethod]
    public async Task Custom_NewInputIsNeverBlocked()
    {
        await ShowFormAsync(Form());

        var events = await RunToEndAsync(Run("run-1", CustomMode, text: "Never mind that"));

        AssertPlainFinish(events);
        Assert.AreEqual("Never mind that", _agent.Messages.Single().Message.Content);
    }

    [TestMethod]
    public async Task Custom_AQuestionOutsideARun_GoesToNotify_AndIsAnsweredOverRest()
    {
        using var notify = await NotifyStream.OpenAsync(_client);
        await notify.ReadConnectedAsync();
        await _agent.FormAsync(Form());
        Assert.AreEqual(TurnEventMapper.CustomNames.McpQuestion, (await notify.ReadAsync()).Name);
        _agent.OnAnswer = (agent, answer) => agent.AcceptAsync(answer, "Booked.");

        using var response = await _client.PostAsync("/questions/q1/answer", Json("""{"threadId":"agui-session","answers":{"tz":"Europe/Brussels"}}"""));

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("q1", body.RootElement.GetProperty("questionId").GetString());
        Assert.AreEqual(Thread, body.RootElement.GetProperty("threadId").GetString());
        Assert.IsTrue(body.RootElement.GetProperty("known").GetBoolean());

        var answer = _agent.Answers.Single();
        Assert.AreEqual("q1", answer.QuestionId);
        Assert.AreEqual(Thread, answer.SessionId);
        Assert.AreEqual("agui-user", answer.UserId);
        Assert.IsFalse(answer.Decline);
        Assert.AreEqual("Europe/Brussels", JsonDocument.Parse(answer.Answers!).RootElement.GetProperty("tz").GetString());

        // No run is waiting, so the status and the reply both arrive on the notify stream, once.
        var notified = await NotifiedAsync(notify);
        CollectionAssert.AreEqual(
            new[]
            {
                TurnEventMapper.CustomNames.McpQuestionStatus,
                "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", TurnEventMapper.CustomNames.Origin,
            },
            Shape(notified));
        Assert.AreEqual("Booked.", Sse.Str(notified[2].Event, "delta"));
        Assert.AreEqual("q1", notified[4].Value.GetProperty("followsQuestionId").GetString());
        Assert.AreEqual(0, Registry.Open(Thread).Count);
    }

    [TestMethod]
    public async Task Custom_Rejected_IsAStatusEvent_AndTheQuestionStaysOpen()
    {
        using var notify = await NotifyStream.OpenAsync(_client);
        await notify.ReadConnectedAsync();
        await _agent.FormAsync(Form());
        await notify.ReadAsync();
        _agent.OnAnswer = (agent, answer) =>
            agent.StatusAsync(answer.QuestionId, McpQuestionState.Rejected, reason: "tz is not a time zone.");

        using var response = await _client.PostAsync("/questions/q1/answer", Json("""{"threadId":"agui-session","answers":{"tz":"Mars"}}"""));

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        var status = await notify.ReadAsync();
        Assert.AreEqual(TurnEventMapper.CustomNames.McpQuestionStatus, status.Name);
        Assert.AreEqual("rejected", status.Value.GetProperty("state").GetString());
        Assert.AreEqual("tz is not a time zone.", status.Value.GetProperty("reason").GetString());
        Assert.AreEqual(1, Registry.Open(Thread).Count);
    }

    // ── POST /questions/{id}/answer ──────────────────────────────────────────

    [TestMethod]
    public async Task Answer_Decline_DeclinesTheQuestion_OnTheThreadTheProxyKnowsItBy()
    {
        await ShowFormAsync(Form());

        // No threadId: the proxy knows which thread q1 is on.
        using var response = await _client.PostAsync("/questions/q1/answer", Json("""{"decline":true,"answers":{"tz":"ignored"}}"""));

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        await UntilAsync(() => _agent.Answers.Count == 1, "the agent received the answer");
        Assert.IsTrue(_agent.Answers[0].Decline);
        Assert.IsNull(_agent.Answers[0].Answers);
        Assert.AreEqual(Thread, _agent.Answers[0].SessionId);
    }

    [TestMethod]
    public async Task Answer_ToAQuestionTheProxyDoesNotKnow_IsPassedToTheAgent()
    {
        // The proxy keeps no state across a restart; the agent decides whether the question exists.
        using var response = await _client.PostAsync("/questions/from-before/answer", Json("""{"threadId":"agui-session","answers":{"tz":"UTC"}}"""));

        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.IsFalse(body.RootElement.GetProperty("known").GetBoolean());
        await UntilAsync(() => _agent.Answers.Count == 1, "the agent received the answer");
        Assert.AreEqual("from-before", _agent.Answers[0].QuestionId);
    }

    [TestMethod]
    public async Task Answer_OnAThreadThatIsNotAllowed_Is403_AndReachesNoAgent()
    {
        await ShowFormAsync(Form());

        using var response = await _client.PostAsync("/questions/q1/answer", Json("""{"threadId":"blazor-session","answers":{"tz":"UTC"}}"""));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        await Task.Delay(100);
        Assert.AreEqual(0, _agent.Answers.Count);
    }

    [TestMethod]
    public async Task Answer_ForAQuestionOnAnotherThread_Is404()
    {
        await RestartAsync(("AgUi:AllowedThreadIds:0", "phone"), ("AgUi:AllowedThreadIds:1", "laptop"));
        await _agent.FormAsync(Form(), sessionId: "phone");
        await UntilAsync(() => Registry.Find("q1") is not null, "the proxy has the question");

        using var response = await _client.PostAsync("/questions/q1/answer", Json("""{"threadId":"laptop","answers":{"tz":"UTC"}}"""));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(0, _agent.Answers.Count);
    }

    [TestMethod]
    public async Task Answer_WithSeveralThreads_AndAnUnknownQuestion_NeedsAThreadId()
    {
        await RestartAsync(("AgUi:AllowedThreadIds:0", "phone"), ("AgUi:AllowedThreadIds:1", "laptop"));

        using var response = await _client.PostAsync("/questions/q1/answer", Json("""{"answers":{"tz":"UTC"}}"""));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    [DataRow("""{"threadId":"agui-session"}""", DisplayName = "neither answers nor decline")]
    [DataRow("""{"threadId":"agui-session","answers":"UTC"}""", DisplayName = "answers that are not an object")]
    [DataRow("""{"threadId":"agui-session","decline":false}""", DisplayName = "decline false and no answers")]
    [DataRow("""["tz"]""", DisplayName = "not an object")]
    [DataRow("""{"threadId":""", DisplayName = "truncated JSON")]
    [DataRow("", DisplayName = "no body")]
    public async Task Answer_WithABadBody_Is400_AndReachesNoAgent(string body)
    {
        await ShowFormAsync(Form());

        using var response = await _client.PostAsync("/questions/q1/answer", Json(body));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(0, _agent.Answers.Count);
    }

    // ── Expiry ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AnExpiredQuestion_NoLongerBlocks_AndIsNotAnInterrupt()
    {
        await ShowFormAsync(Form(expiresIn: TimeSpan.FromMinutes(2)));
        Assert.AreEqual(TurnEventMapper.ErrorCodes.InterruptsPending, Code(await RunToEndAsync(Run("run-1", InterruptMode))));

        // No status from the agent: the proxy drops the question by its own clock.
        _clock.Advance(TimeSpan.FromMinutes(2));

        AssertPlainFinish(await RunToEndAsync(Run("run-2", InterruptMode)));
        Assert.AreEqual(1, _agent.Messages.Count);
        Assert.AreEqual(0, Registry.Open(Thread).Count);
    }

    [TestMethod]
    public async Task AQuestionTheAgentReportsExpired_NoLongerBlocks()
    {
        await ShowFormAsync(Form());

        await _agent.StatusAsync("q1", McpQuestionState.Expired, reason: "Nobody answered in time.");
        await UntilAsync(() => Registry.Open(Thread).Count == 0, "the question is settled");

        AssertPlainFinish(await RunToEndAsync(Run("run-1", InterruptMode)));
    }

    [TestMethod]
    public async Task AQuestionThatExpiresDuringTheRun_IsNotCarriedAsAnInterrupt()
    {
        _agent.OnMessage = async (agent, received) =>
        {
            await ShowFormAsync(Form("brief", expiresIn: TimeSpan.FromSeconds(30)));
            await ShowFormAsync(Form("lasting"));
            _clock.Advance(TimeSpan.FromSeconds(31));
            await agent.ReplyAsync(received, "Please fill in the form.");
        };

        var interrupts = Interrupts(await RunToEndAsync(Run("run-1", InterruptMode)));

        Assert.AreEqual("lasting", interrupts.Single().GetProperty("id").GetString());
    }

    // ── The reply to an answer ───────────────────────────────────────────────

    [TestMethod]
    public async Task AFollowUpNobodyWaitsFor_ArrivesOnNotifyOnce_EvenWhileAnotherRunIsOpen()
    {
        using var notify = await NotifyStream.OpenAsync(_client);
        await notify.ReadConnectedAsync();

        // A run with new input is open when the reply to an earlier form answer arrives.
        _agent.OnMessage = async (agent, received) =>
        {
            await agent.FollowUpAsync("q0", "About that earlier form: booked.");
            await UntilAsync(() => _notifiedFollowUp, "the follow-up reached the notify stream");
            await agent.ReplyAsync(received, "And here is your answer.");
        };
        var run = RunToEndAsync(Run("run-1", CustomMode));

        var frames = await notify.ReadAsync(4);
        _notifiedFollowUp = true;
        CollectionAssert.AreEqual(
            new[] { "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", TurnEventMapper.CustomNames.Origin },
            Shape(frames));
        Assert.AreEqual("About that earlier form: booked.", Sse.Str(frames[1].Event, "delta"));

        // The run has its own reply and not the follow-up.
        var events = await run.WaitAsync(Patience);
        CollectionAssert.AreEqual(
            new[] { "And here is your answer." },
            events.Where(e => Sse.Type(e) == "TEXT_MESSAGE_CONTENT").Select(e => Sse.Str(e, "delta")).ToArray());
        Assert.AreEqual(0, (await NotifiedAsync(notify)).Count);
    }

    private volatile bool _notifiedFollowUp;
}

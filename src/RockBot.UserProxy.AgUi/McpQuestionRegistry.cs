using System.Threading.Channels;

namespace RockBot.UserProxy.AgUi;

/// <summary>How a run takes part in MCP questions. See <c>docs/agui.md</c>.</summary>
internal enum QuestionMode
{
    /// <summary>The request didn't declare <see cref="ClientCapabilities.InteractiveForms"/>: no forms, so nothing to do.</summary>
    None,

    /// <summary>AG-UI's own way: open questions end the run as interrupts and block new input until a run resumes them.</summary>
    Interrupt,

    /// <summary>RockBot's way, as in the Blazor UI: questions are events, answered over REST, and nothing is blocked.</summary>
    Custom,
}

/// <summary>A question the agent has shown as a form and not yet reported settled.</summary>
internal sealed class OpenQuestion(string threadId, McpQuestionForm form, AgentReply reply)
{
    public string ThreadId => threadId;
    public McpQuestionForm Form => form;

    /// <summary>The reply that carried the form: its text rendering, agent name and raw form JSON.</summary>
    public AgentReply Reply => reply;

    /// <summary>
    /// True once the question has been put where a client can see it: a run's stream, a run's
    /// interrupt outcome, or the notify stream. Guarded by the registry's lock.
    /// </summary>
    internal bool Announced { get; set; }
}

/// <summary>What a run that resumed questions is told about them.</summary>
internal abstract record ResumeSignal;

/// <summary>The agent reported a resumed question's state.</summary>
internal sealed record QuestionStatusSignal(McpQuestionStatus Status) : ResumeSignal;

/// <summary>The agent's reply to an accepted answer arrived.</summary>
internal sealed record FollowUpSignal(AgentReply Reply) : ResumeSignal;

/// <summary>One open run's registration with the <see cref="McpQuestionRegistry"/>.</summary>
internal sealed class RunQuestions(string threadId, QuestionMode mode, TurnEventEmitter emitter)
{
    private readonly Channel<ResumeSignal> _signals =
        Channel.CreateUnbounded<ResumeSignal>(new UnboundedChannelOptions { SingleReader = true });

    public string ThreadId => threadId;
    public QuestionMode Mode => mode;
    public TurnEventEmitter Emitter => emitter;

    /// <summary>For a resume run: statuses and follow-up replies of the questions it answered. Completed when the run is ended.</summary>
    public ChannelReader<ResumeSignal> Signals => _signals.Reader;

    internal ChannelWriter<ResumeSignal> SignalWriter => _signals.Writer;

    /// <summary>Guarded by the registry's lock.</summary>
    internal bool Ended { get; set; }
}

/// <summary>How <see cref="McpQuestionRegistry.TryBeginResume"/> went.</summary>
/// <param name="Run">The registration, when the resume may go ahead.</param>
/// <param name="Unanswered">Open questions the resume doesn't address; the run is refused.</param>
/// <param name="AllOpen">Every open question, for telling the client what it has to answer.</param>
/// <param name="BusyQuestionId">A question another run is already resuming; the request is refused.</param>
internal sealed record ResumeStart(
    RunQuestions? Run,
    IReadOnlyList<OpenQuestion> Unanswered,
    IReadOnlyList<OpenQuestion> AllOpen,
    string? BusyQuestionId);

/// <summary>
/// The proxy's view of MCP questions (#794, phase 3): for each allowed thread, the questions the
/// agent has shown as forms and not yet settled, and the runs open on it. It decides where a form
/// goes, whether new input is blocked, and which run gets the reply that follows an answer.
/// </summary>
/// <remarks>
/// <para>
/// The agent owns a question; this is a cache built from what the proxy hears on the bus: a form
/// opens one, an <see cref="McpQuestionStatus"/> that says it is closed removes it, and it drops
/// out by itself when it expires. Nothing is persisted. After a restart the proxy knows of no open
/// questions: nothing is blocked, and an answer to a question it doesn't know is passed to the
/// agent, which is the one that can tell.
/// </para>
/// <para>
/// Forms and statuses arrive on the bus's message-handling thread, runs start and end on request
/// threads. One lock covers everything, held only for in-memory work: the notify hub and a run's
/// emitter only queue, and neither calls back into the registry.
/// </para>
/// </remarks>
internal sealed class McpQuestionRegistry
{
    private sealed class ThreadQuestions(string threadId)
    {
        public string ThreadId => threadId;

        /// <summary>In the order they arrived.</summary>
        public OrderedDictionary<string, OpenQuestion> Open { get; } = new(StringComparer.Ordinal);

        public List<RunQuestions> Runs { get; } = [];
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ThreadQuestions> _threads = new(StringComparer.Ordinal);

    /// <summary>The run waiting on each resumed question: for its status, then for the reply that follows.</summary>
    private readonly Dictionary<string, RunQuestions> _resuming = new(StringComparer.Ordinal);

    private readonly NotifyHub _hub;
    private readonly TimeProvider _clock;

    public McpQuestionRegistry(AgUiOptions options, NotifyHub hub, TimeProvider? clock = null)
    {
        _hub = hub;
        _clock = clock ?? TimeProvider.System;
        foreach (var threadId in options.ThreadIds())
            _threads[threadId] = new ThreadQuestions(threadId);
    }

    // ── From the bus ─────────────────────────────────────────────────────────

    /// <summary>
    /// A question form arrived. It goes into each open run on its thread that takes forms: a
    /// custom-mode run shows it at once, an interrupt-mode run holds it for its
    /// <c>RUN_FINISHED</c>. With no such run it goes to the notify stream.
    /// </summary>
    public void OnForm(AgentReply reply)
    {
        var form = McpQuestionInterrupts.TryParseForm(reply.StructuredData);
        lock (_lock)
        {
            // Not a form this proxy can read, or not one of its threads (the hub drops the
            // latter): passed on as it is, and not tracked.
            if (form is null || FindThread(reply.SessionId) is not { } thread)
            {
                _hub.PublishToSession(reply.SessionId, m => [McpQuestionInterrupts.QuestionEvent(m, reply, form)]);
                return;
            }

            Prune(thread);
            var question = new OpenQuestion(thread.ThreadId, form, reply);
            thread.Open[form.QuestionId] = question;

            var held = false;
            foreach (var run in thread.Runs)
            {
                if (run.Mode == QuestionMode.Custom)
                {
                    run.Emitter.Emit(m => [McpQuestionInterrupts.QuestionEvent(m, reply, form)]);
                    question.Announced = true;
                }
                else if (run.Mode == QuestionMode.Interrupt)
                {
                    held = true;
                }
            }

            // Held: the interrupt-mode run announces it when it ends (see EndRun).
            if (!question.Announced && !held)
                Announce(question);
        }
    }

    /// <summary>
    /// A question changed state. A closed one is forgotten; <c>rejected</c> leaves it open. The
    /// run that resumed it, if any, is told. (The notify stream gets every status as well; that
    /// is the frontend's business.)
    /// </summary>
    public void OnStatus(McpQuestionStatus status)
    {
        lock (_lock)
        {
            if (McpQuestionState.IsClosed(status.State))
            {
                foreach (var thread in _threads.Values)
                    thread.Open.Remove(status.QuestionId);
            }

            if (_resuming.TryGetValue(status.QuestionId, out var run))
                run.SignalWriter.TryWrite(new QuestionStatusSignal(status));
        }
    }

    /// <summary>
    /// The agent's reply to a form answer (<see cref="AgentReply.FollowsQuestionId"/>). True when a
    /// run that resumed the question is waiting for it and took it; false when nobody is, and
    /// the caller puts it on the notify stream. Decided under the lock that ends a run, so the
    /// reply goes to exactly one of the two.
    /// </summary>
    public bool TryRouteFollowUp(AgentReply reply)
    {
        lock (_lock)
        {
            return reply.FollowsQuestionId is { } questionId
                && _resuming.Remove(questionId, out var run)
                && run.SignalWriter.TryWrite(new FollowUpSignal(reply));
        }
    }

    /// <summary>
    /// Puts a follow-up reply on the notify stream after all: the run that took it ended (its
    /// client left) before it could write it.
    /// </summary>
    public void PublishUnclaimedFollowUp(AgentReply reply) =>
        _hub.PublishToSession(reply.SessionId, m => NotifyEvents.Reply(m, reply));

    // ── Runs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a run with new input. An interrupt-mode run is refused while its thread has open
    /// questions, which AG-UI requires to be resumed first: the result is then null and
    /// <paramref name="blockedBy"/> lists them.
    /// </summary>
    public RunQuestions? TryBeginRun(
        string threadId, QuestionMode mode, TurnEventEmitter emitter, out IReadOnlyList<OpenQuestion> blockedBy)
    {
        lock (_lock)
        {
            var thread = _threads[threadId];
            Prune(thread);
            if (mode == QuestionMode.Interrupt && thread.Open.Count > 0)
            {
                blockedBy = Announced(thread.Open.Values);
                return null;
            }

            blockedBy = [];
            var run = new RunQuestions(threadId, mode, emitter);
            thread.Runs.Add(run);
            return run;
        }
    }

    /// <summary>
    /// Registers a run that resumes <paramref name="questionIds"/>. It must address every question
    /// open on the thread, and none that another run is resuming. An id the proxy doesn't know
    /// (it restarted, or the question has just expired here) is allowed: the answer goes to the
    /// agent, which reports what became of it.
    /// </summary>
    public ResumeStart TryBeginResume(string threadId, IReadOnlyCollection<string> questionIds, TurnEventEmitter emitter)
    {
        lock (_lock)
        {
            var thread = _threads[threadId];
            Prune(thread);

            if (questionIds.FirstOrDefault(_resuming.ContainsKey) is { } busy)
                return new ResumeStart(null, [], [], busy);

            var unanswered = thread.Open.Values.Where(q => !questionIds.Contains(q.Form.QuestionId)).ToList();
            if (unanswered.Count > 0)
                return new ResumeStart(null, unanswered, Announced(thread.Open.Values), null);

            var run = new RunQuestions(threadId, QuestionMode.Interrupt, emitter);
            thread.Runs.Add(run);
            foreach (var questionId in questionIds)
                _resuming[questionId] = run;
            return new ResumeStart(run, [], [], null);
        }
    }

    /// <summary>
    /// A resume run stops waiting for one question (it settled without a reply, or the reply took
    /// too long). A reply that still comes goes to the notify stream.
    /// </summary>
    public void StopWaiting(RunQuestions run, string questionId)
    {
        lock (_lock)
        {
            if (_resuming.TryGetValue(questionId, out var waiting) && ReferenceEquals(waiting, run))
                _resuming.Remove(questionId);
        }
    }

    /// <summary>
    /// Takes a run out, before its terminal event is written. With
    /// <paramref name="carryInterrupts"/>, an interrupt-mode run gets the thread's open questions
    /// to end with. Otherwise (the run failed or was cancelled, or isn't in interrupt mode) the
    /// questions it was holding go to the notify stream, so they aren't lost. Only the first call
    /// for a run does anything.
    /// </summary>
    public IReadOnlyList<OpenQuestion> EndRun(RunQuestions run, bool carryInterrupts)
    {
        lock (_lock)
        {
            if (run.Ended)
                return [];
            run.Ended = true;

            var thread = _threads[run.ThreadId];
            thread.Runs.Remove(run);
            foreach (var questionId in _resuming.Where(pair => ReferenceEquals(pair.Value, run)).Select(pair => pair.Key).ToList())
                _resuming.Remove(questionId);
            run.SignalWriter.TryComplete();

            Prune(thread);
            if (carryInterrupts && run.Mode == QuestionMode.Interrupt)
                return Announced(thread.Open.Values);

            // Nothing is left holding them: another interrupt-mode run would have carried them.
            if (!thread.Runs.Any(r => r.Mode == QuestionMode.Interrupt))
            {
                foreach (var question in thread.Open.Values.Where(q => !q.Announced))
                    Announce(question);
            }
            return [];
        }
    }

    // ── Lookups ──────────────────────────────────────────────────────────────

    /// <summary>The open question with this id, on whichever thread, or null.</summary>
    public OpenQuestion? Find(string questionId)
    {
        lock (_lock)
        {
            foreach (var thread in _threads.Values)
            {
                Prune(thread);
                if (thread.Open.TryGetValue(questionId, out var question))
                    return question;
            }
            return null;
        }
    }

    /// <summary>The thread's open questions, oldest first. Empty for a thread that isn't allowed.</summary>
    public IReadOnlyList<OpenQuestion> Open(string threadId)
    {
        lock (_lock)
        {
            if (!_threads.TryGetValue(threadId, out var thread))
                return [];
            Prune(thread);
            return [.. thread.Open.Values];
        }
    }

    // ── Under the lock ───────────────────────────────────────────────────────

    private ThreadQuestions? FindThread(string? sessionId) =>
        _threads.Values.FirstOrDefault(t => SessionRouting.IsSessionsReply(t.ThreadId, sessionId));

    /// <summary>
    /// Forgets the questions whose time is up. The agent reports an expiry too, but a status can
    /// be missed (the proxy was reconnecting), and a question that can no longer be answered must
    /// not block the thread.
    /// </summary>
    private void Prune(ThreadQuestions thread)
    {
        if (thread.Open.Count == 0)
            return;

        var now = _clock.GetUtcNow();
        foreach (var expired in thread.Open.Values.Where(q => q.Form.ExpiresAt <= now).Select(q => q.Form.QuestionId).ToList())
            thread.Open.Remove(expired);
    }

    private void Announce(OpenQuestion question)
    {
        question.Announced = true;
        _hub.PublishToThread(question.ThreadId,
            m => [McpQuestionInterrupts.QuestionEvent(m, question.Reply, question.Form)]);
    }

    /// <summary>The questions, marked as shown to a client: the caller is about to put them in a run's stream.</summary>
    private static List<OpenQuestion> Announced(IEnumerable<OpenQuestion> questions)
    {
        var list = questions.ToList();
        foreach (var question in list)
            question.Announced = true;
        return list;
    }
}

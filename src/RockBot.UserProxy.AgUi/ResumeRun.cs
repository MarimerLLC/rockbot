using System.Diagnostics;
using System.Threading.Channels;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi;

/// <summary>
/// A run that resumes interrupts (#794, phase 3): it sends one <see cref="McpQuestionAnswer"/> for
/// each <c>resume</c> entry, streams what the agent makes of them, and ends when every answered
/// question is settled and the reply that follows each accepted answer has arrived.
/// </summary>
/// <remarks>
/// What the agent sends back for an answer, all of it broadcast and uncorrelated:
/// <list type="bullet">
///   <item><c>rejected</c>: the answer was refused (it failed the server's schema, say) and the
///   question is still open. The run ends with <c>RUN_ERROR</c> so the client can answer again.</item>
///   <item><c>answered</c> or <c>declined</c> by the user: the resumed MCP call goes on, and the
///   agent's reply about its result follows, with <see cref="AgentReply.FollowsQuestionId"/>.</item>
///   <item>Any other state (<c>unavailable</c>, <c>expired</c>, <c>cancelled</c>,
///   <c>interrupted</c>, or answered by the agent itself): the question is closed and nothing
///   follows.</item>
/// </list>
/// The reply is not guaranteed: an answer that goes on to a background task gets none. So the wait
/// for it is bounded, and a run that gives up ends as a long turn does, with
/// <c>rockbot.reply_pending</c>; the reply, if it comes, is then on the notify stream.
/// </remarks>
internal sealed class ResumeRun(
    UserProxyService proxy,
    McpQuestionRegistry questions,
    RunQuestions run,
    TurnEventEmitter emitter,
    ChannelWriter<BaseEvent> events,
    ILogger logger,
    string userId,
    TimeSpan statusTimeout,
    TimeSpan replyTimeout)
{
    /// <summary>What the run still waits for, for one question.</summary>
    private sealed class Waiting(long deadline)
    {
        /// <summary>False while waiting for the agent's status; true once it accepted the answer and the reply is awaited.</summary>
        public bool Accepted { get; set; }

        /// <summary>A <see cref="Stopwatch"/> timestamp.</summary>
        public long Deadline { get; set; } = deadline;
    }

    /// <summary>Answers the interrupts and streams the outcome. Never throws; always completes the channel.</summary>
    public async Task RunAsync(IReadOnlyList<AgUiResumeEntry> entries, CancellationToken stop, CancellationToken clientGone)
    {
        try
        {
            emitter.Start();

            var waiting = new Dictionary<string, Waiting>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                await proxy.SendMcpAnswerAsync(new McpQuestionAnswer
                {
                    SessionId = run.ThreadId,
                    UserId = userId,
                    QuestionId = entry.QuestionId,
                    Answers = entry.Answers,
                    Decline = entry.Decline,
                }, stop);
                waiting[entry.QuestionId] = new Waiting(After(statusTimeout));
            }

            // Questions whose reply the run stopped waiting for.
            var overdue = new HashSet<string>(StringComparer.Ordinal);

            while (waiting.Count > 0)
            {
                var (questionId, next) = waiting.MinBy(pair => pair.Value.Deadline);
                var signal = await NextSignalAsync(next.Deadline, stop);

                if (signal is null)
                {
                    if (!next.Accepted)
                    {
                        // No word from the agent at all. The question is still open as far as the
                        // proxy knows, so the client can answer it again.
                        End(carryInterrupts: false);
                        emitter.Fail(TurnEventMapper.ErrorCodes.AgentLost,
                            $"The agent did not acknowledge the answer to interrupt '{questionId}'. It is still open; resume it again.");
                        return;
                    }

                    questions.StopWaiting(run, questionId);
                    waiting.Remove(questionId);
                    overdue.Add(questionId);
                    continue;
                }

                if (Handle(signal, waiting, overdue) is { } rejected)
                {
                    End(carryInterrupts: false);
                    emitter.Fail(TurnEventMapper.ErrorCodes.InterruptRejected,
                        $"The answer to interrupt '{rejected.QuestionId}' was not accepted: "
                        + (rejected.Reason is { Length: > 0 } reason ? reason : "no reason given")
                        + " It is still open; resume it again.");
                    return;
                }
            }

            // A question the server asked after an answer has arrived as a new form by now (the
            // bridge sends it before the agent's reply): it ends this run as an interrupt.
            var interrupts = AgUiEndpoints.Interrupts(End(carryInterrupts: true, waiting, overdue));
            if (overdue.Count > 0)
            {
                emitter.FinishReplyPending(new TurnResult
                {
                    Outcome = TurnResultOutcome.TimedOut,
                    CorrelationId = emitter.RunId,
                    Error = "The agent has not replied to the answer yet. Its reply, if one comes, arrives on the notify stream.",
                }, interrupts);
            }
            else
            {
                emitter.Finish(new TurnResult { Outcome = TurnResultOutcome.Success, CorrelationId = emitter.RunId }, interrupts);
            }
        }
        catch (OperationCanceledException) when (clientGone.IsCancellationRequested)
        {
            // Unlike a run with new input, an abort here cancels nothing. The answers are sent and
            // can't be taken back, and cancelling the session would stop the very work they
            // released. The reply goes to the notify stream instead.
            End(carryInterrupts: false, toNotify: true);
            logger.LogInformation("Client left resume run {RunId} on thread {ThreadId}; the agent's reply goes to the notify stream",
                emitter.RunId, emitter.ThreadId);
        }
        catch (OperationCanceledException)
        {
            End(carryInterrupts: false);
            logger.LogInformation("Ending resume run {RunId} on thread {ThreadId}: the proxy is stopping", emitter.RunId, emitter.ThreadId);
            emitter.Fail(AgUiEndpoints.ProxyStoppingCode, "The proxy is shutting down. The agent may still reply to the answer.");
        }
        catch (Exception ex)
        {
            End(carryInterrupts: false);
            logger.LogError(ex, "Resume run {RunId} on thread {ThreadId} failed", emitter.RunId, emitter.ThreadId);
            emitter.Fail(TurnEventMapper.ErrorCodes.ClientError, ex.Message);
        }
        finally
        {
            End(carryInterrupts: false);
            events.TryComplete();
        }
    }

    /// <summary>The next signal, or null when <paramref name="deadline"/> passes first.</summary>
    private async Task<ResumeSignal?> NextSignalAsync(long deadline, CancellationToken stop)
    {
        if (run.Signals.TryRead(out var ready))
            return ready;

        var left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
        if (left <= TimeSpan.Zero)
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(left);
        try
        {
            return await run.Signals.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Streams a signal and updates what the run waits for. Returns the status when it is a
    /// rejection, which ends the run.
    /// </summary>
    private McpQuestionStatus? Handle(ResumeSignal signal, Dictionary<string, Waiting> waiting, HashSet<string> overdue)
    {
        switch (signal)
        {
            case FollowUpSignal { Reply: var reply }:
                // The reply as a message, then rockbot.origin, which names the question it follows.
                emitter.UnsolicitedReply(reply);
                if (reply.FollowsQuestionId is { } followed)
                {
                    waiting.Remove(followed);
                    overdue.Remove(followed);
                }
                return null;

            case QuestionStatusSignal { Status: var status }:
                emitter.Emit(m => [NotifyEvents.McpQuestionStatus(m, status)]);
                if (!waiting.TryGetValue(status.QuestionId, out var entry))
                    return null;

                if (!McpQuestionState.IsClosed(status.State))
                    return status;

                // Only an answer the user's form gave leads to a reply. One the agent gave itself
                // (mcp_answer, racing the form) is part of some other turn.
                var replyFollows = status.State is McpQuestionState.Answered or McpQuestionState.Declined
                    && !string.Equals(status.AnsweredBy, "agent", StringComparison.OrdinalIgnoreCase);
                if (replyFollows)
                {
                    entry.Accepted = true;
                    entry.Deadline = After(replyTimeout);
                }
                else
                {
                    questions.StopWaiting(run, status.QuestionId);
                    waiting.Remove(status.QuestionId);
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Takes the run out of the registry and streams what was already on its way: a reply handed
    /// to this run just before it stopped waiting would otherwise reach nobody. With
    /// <paramref name="toNotify"/> (the client is gone) such a reply goes to the notify stream
    /// instead. Only the first call does anything.
    /// </summary>
    private IReadOnlyList<OpenQuestion> End(
        bool carryInterrupts, Dictionary<string, Waiting>? waiting = null, HashSet<string>? overdue = null,
        bool toNotify = false)
    {
        var open = questions.EndRun(run, carryInterrupts);
        while (run.Signals.TryRead(out var signal))
        {
            if (toNotify && signal is FollowUpSignal { Reply: var reply })
                questions.PublishUnclaimedFollowUp(reply);
            else if (!toNotify)
                Handle(signal, waiting ?? [], overdue ?? []);
        }
        return open;
    }

    private static long After(TimeSpan time) =>
        Stopwatch.GetTimestamp() + (long)(time.TotalSeconds * Stopwatch.Frequency);
}

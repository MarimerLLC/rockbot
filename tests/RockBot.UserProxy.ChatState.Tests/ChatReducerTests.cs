using static RockBot.UserProxy.ChatState.Tests.ChatEventFixtures;

namespace RockBot.UserProxy.ChatState.Tests;

/// <summary>Reducer tests over recorded event sequences.</summary>
[TestClass]
public class ChatReducerTests
{
    // ── A normal turn ────────────────────────────────────────────────────

    [TestMethod]
    public void NormalTurn_ProgressGoesToOneLog_FinalReplyClosesItAndEndsTheTurn()
    {
        var running = ChatReducer.Replay(
        [
            new UserMessageSent("What's on today?", "u1", Session) { Timestamp = At(0), MessageId = "user-1" },
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnProgress("turn-1", "Calling get_events…") { Timestamp = At(1), MessageId = "log-1" },
            new TurnProgress("turn-1", "get_events → 3 events") { Timestamp = At(2), MessageId = "log-ignored" },
        ]);

        Assert.IsTrue(running.IsBusy(At(3)));
        Assert.IsTrue(running.IsActivityLogOpen("log-1"));
        Assert.IsTrue(running.HasOpenActivityLog);

        var done = ChatReducer.Reduce(running,
            new TurnEnded("turn-1", TurnOutcome.Completed) { Reply = Reply("Three meetings."), Timestamp = At(4) });

        Assert.AreEqual(3, done.Messages.Count);
        var (user, log, reply) = (done.Messages[0], done.Messages[1], done.Messages[2]);

        Assert.AreEqual(MessageCategory.UserInput, user.Category);
        Assert.IsTrue(user.IsFromUser);

        Assert.AreEqual("log-1", log.MessageId);
        Assert.IsTrue(log.IsActivityLog);
        Assert.AreEqual(MessageCategory.PrimaryProgress, log.Category);
        CollectionAssert.AreEqual(
            new[] { "Calling get_events…", "get_events → 3 events" },
            log.ActivityLogEntries.Select(e => e.Content).ToArray());
        Assert.AreEqual("get_events → 3 events", log.Content, "the summary line is the latest entry");
        Assert.IsFalse(done.IsActivityLogOpen("log-1"));

        Assert.AreEqual(MessageCategory.PrimaryFinal, reply.Category);
        Assert.AreEqual("Three meetings.", reply.Content);
        Assert.IsTrue(reply.IsExpanded);
        Assert.IsFalse(done.IsBusy(At(5)));
        Assert.IsNull(done.CurrentTurn);
    }

    [TestMethod]
    public void TurnFailed_ShowsErrorAndEndsTheTurn()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnEnded("turn-1", TurnOutcome.Failed) { Error = "bus down", Timestamp = At(1) },
        ]);

        var error = state.Messages.Single();
        Assert.IsTrue(error.IsError);
        Assert.AreEqual("Error: bus down", error.Content);
        Assert.IsFalse(state.IsBusy(At(2)));
    }

    // ── A timeout followed by a late reply ───────────────────────────────

    [TestMethod]
    public void Timeout_ShowsErrorAndReleasesInput_LateReplyStillLandsAndClosesTheLog()
    {
        var timedOut = ChatReducer.Replay(
        [
            new UserMessageSent("Research this", "u1", Session) { Timestamp = At(0) },
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnProgress("turn-1", "Searching…") { Timestamp = At(10), MessageId = "log-1" },
            new TurnEnded("turn-1", TurnOutcome.TimedOut) { Timestamp = At(180), MessageId = "timeout" },
        ]);

        var error = timedOut.Messages[^1];
        Assert.AreEqual("timeout", error.MessageId);
        Assert.IsTrue(error.IsError);
        Assert.AreEqual("No reply received (timeout)", error.Content);
        Assert.IsFalse(timedOut.IsBusy(At(181)), "the input is released on timeout");
        Assert.IsTrue(timedOut.IsActivityLogOpen("log-1"), "the agent may still be working");

        // The agent finishes after the proxy gave up: the reply arrives unsolicited.
        var late = ChatReducer.Reduce(timedOut,
            new ReplyReceived(Reply("Here's the research."), MessageCategory.PrimaryFinal) { Timestamp = At(240) });

        Assert.AreEqual(MessageCategory.PrimaryFinal, late.Messages[^1].Category);
        Assert.AreEqual("Here's the research.", late.Messages[^1].Content);
        Assert.AreSame(error, late.Messages[^2], "the reply follows the timeout error");
        Assert.IsFalse(late.IsActivityLogOpen("log-1"));
        Assert.IsFalse(late.IsBusy(At(241)));
    }

    // ── A subagent run ───────────────────────────────────────────────────

    [TestMethod]
    public void SubagentRun_LogsUnderItsOwnSource_CompletionClosesIt_ResultFollows()
    {
        var running = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new TurnProgress("turn-1", "Spawning subagent") { Timestamp = At(1), MessageId = "primary-log" },
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-abc", "Searching the web…")
                { Timestamp = At(2), MessageId = "sub-log" },
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-abc", "Reading 3 pages…")
                { Timestamp = At(3) },
        ]);

        Assert.IsTrue(running.IsActivityLogOpen("primary-log"));
        Assert.IsTrue(running.IsActivityLogOpen("sub-log"));
        var indicator = running.ActiveStatusIndicators.Single();
        Assert.AreEqual(MessageCategory.SubagentActivity, indicator.Category);
        Assert.AreEqual("subagent-abc", indicator.AgentName);
        Assert.AreEqual("Reading 3 pages…", indicator.LatestContent);

        var finished = ChatReducer.Replay(
        [
            // The primary turn ends while the subagent keeps going.
            new TurnEnded("turn-1", TurnOutcome.Completed) { Reply = Reply("I've started a subagent."), Timestamp = At(4) },
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-abc", "Done", IsCompletion: true)
                { Timestamp = At(60) },
            new ReplyReceived(Reply("Found 3 sources.", isFinal: false, agentName: "subagent-abc"),
                MessageCategory.SubagentActivity) { Timestamp = At(61) },
        ], running);

        Assert.IsFalse(finished.IsActivityLogOpen("primary-log"));
        Assert.IsFalse(finished.IsActivityLogOpen("sub-log"));
        Assert.AreEqual(0, finished.ActiveStatusIndicators.Count);
        Assert.AreEqual(3, finished.FindMessage("sub-log")!.ActivityLogEntries.Count);

        var result = finished.Messages[^1];
        Assert.AreEqual(MessageCategory.SubagentActivity, result.Category);
        Assert.IsTrue(result.IsInterim);
        Assert.IsFalse(result.IsExpanded, "subagent results start collapsed");
    }

    [TestMethod]
    public void SubagentResult_ClosesOnlyItsOwnLog()
    {
        var state = ChatReducer.Replay(
        [
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-a", "a working") { Timestamp = At(0), MessageId = "a" },
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-b", "b working") { Timestamp = At(1), MessageId = "b" },
            new ReplyReceived(Reply("a done", agentName: "subagent-a"), MessageCategory.SubagentActivity) { Timestamp = At(2) },
        ]);

        Assert.IsFalse(state.IsActivityLogOpen("a"));
        Assert.IsTrue(state.IsActivityLogOpen("b"));
    }

    [TestMethod]
    public void ActiveStatusReconciled_SeedsRunningSubagents_DropsFinishedOnes_AdoptsBusyFlag()
    {
        var stale = ChatReducer.Replay(
        [
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-old", "still here?") { Timestamp = At(0), MessageId = "old" },
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-kept", "working") { Timestamp = At(1), MessageId = "kept" },
        ]);

        var reconciled = ChatReducer.Reduce(stale, new ActiveStatusReconciled(new ActiveStatusResponse
        {
            IsProcessing = true,
            Subagents =
            [
                new ActiveSubagentInfo { TaskId = "kept", Description = "kept task", StartedAt = At(1) },
                new ActiveSubagentInfo { TaskId = "new", Description = "summarise inbox", StartedAt = At(5) },
            ],
        }) { Timestamp = At(10), IdPrefix = "seed" });

        Assert.IsFalse(reconciled.IsActivityLogOpen("old"), "finished subagent's log is closed");
        Assert.IsTrue(reconciled.IsActivityLogOpen("kept"), "known running subagent keeps its log");

        var seeded = reconciled.FindMessage("seed-new");
        Assert.IsNotNull(seeded);
        Assert.IsTrue(reconciled.IsActivityLogOpen("seed-new"));
        Assert.AreEqual("subagent-new", seeded.AgentName);
        Assert.AreEqual("Running: summarise inbox", seeded.Content);
        Assert.AreEqual(At(5), seeded.Timestamp);
        Assert.AreEqual(1, seeded.ActivityLogEntries.Count);

        Assert.AreEqual(ChatViewState.ReconciledTurnId, reconciled.CurrentTurn?.TurnId);
        Assert.IsTrue(reconciled.IsBusy(At(11)));

        var idle = ChatReducer.Reduce(reconciled, new ActiveStatusReconciled(new ActiveStatusResponse
        {
            IsProcessing = false,
            Subagents = [],
        }) { Timestamp = At(20) });

        Assert.IsFalse(idle.IsBusy(At(21)));
        Assert.AreEqual(0, idle.ActiveStatusIndicators.Count);
    }

    [TestMethod]
    public void ActiveStatusReconciled_KeepsTheTurnThisViewStarted()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new ActiveStatusReconciled(new ActiveStatusResponse { IsProcessing = true, Subagents = [] }) { Timestamp = At(1) },
        ]);

        Assert.AreEqual("turn-1", state.CurrentTurn?.TurnId);
    }

    // ── The MCP question lifecycle ───────────────────────────────────────

    [TestMethod]
    public void McpQuestion_AnsweredWhileTurnRuns_StaysBusyUntilTheFollowUpReply()
    {
        var asked = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            Question("q1", atSeconds: 1),
        ]);

        var form = asked.FindQuestion("q1");
        Assert.IsNotNull(form);
        Assert.AreEqual(MessageCategory.McpQuestion, form.Category);
        Assert.IsTrue(form.IsQuestionOpen(new DateTimeOffset(At(2))));
        Assert.AreEqual("turn-1", asked.CurrentTurn?.TurnId, "a question doesn't end the turn");

        // The same question again (a resend) is ignored.
        var duplicate = ChatReducer.Apply(asked, Question("q1", atSeconds: 2));
        Assert.IsFalse(duplicate.Accepted);
        Assert.AreSame(asked, duplicate.State);

        var submitted = ChatReducer.Apply(asked, new McpAnswerSubmitted("q1") { Timestamp = At(3) });
        Assert.IsTrue(submitted.Accepted);
        Assert.IsTrue(submitted.State.FindQuestion("q1")!.IsSubmitting);

        // A second tab sending the same answer is refused.
        Assert.IsFalse(ChatReducer.Apply(submitted.State, new McpAnswerSubmitted("q1") { Timestamp = At(4) }).Accepted);

        var answered = ChatReducer.Replay(
        [
            QuestionStatus("q1", McpQuestionState.Answered, atSeconds: 5),
            // The turn that showed the form finishes first; that must not end the form's wait.
            new TurnEnded("turn-1", TurnOutcome.Completed) { Reply = Reply("Waiting on your answer."), Timestamp = At(6) },
        ], submitted.State);

        var settled = answered.FindQuestion("q1")!;
        Assert.AreEqual(McpQuestionState.Answered, settled.QuestionState);
        Assert.IsFalse(settled.IsSubmitting);
        Assert.IsNull(answered.CurrentTurn);
        Assert.IsTrue(answered.IsBusy(At(7)), "still waiting for the follow-up to the answer");

        var followUp = ChatReducer.Reduce(answered,
            new ReplyReceived(Reply("Event created.", followsQuestionId: "q1"), MessageCategory.PrimaryFinal) { Timestamp = At(8) });

        Assert.IsFalse(followUp.IsBusy(At(9)));
        Assert.AreEqual("Event created.", followUp.Messages[^1].Content);
    }

    [TestMethod]
    public void McpQuestion_RejectedAnswer_ReopensTheFormAndEndsTheWait()
    {
        var state = ChatReducer.Replay(
        [
            Question("q1", atSeconds: 0),
            new McpAnswerSubmitted("q1") { Timestamp = At(1) },
            QuestionStatus("q1", McpQuestionState.Rejected, atSeconds: 2, reason: "date is required"),
        ]);

        var form = state.FindQuestion("q1")!;
        Assert.AreEqual(McpQuestionState.Rejected, form.QuestionState);
        Assert.AreEqual("date is required", form.QuestionReason);
        Assert.IsTrue(form.IsQuestionOpen(new DateTimeOffset(At(3))));
        Assert.IsFalse(state.IsBusy(At(3)));

        var retry = ChatReducer.Apply(state, new McpAnswerSubmitted("q1") { Timestamp = At(4) });
        Assert.IsTrue(retry.Accepted);
        Assert.IsNull(retry.State.FindQuestion("q1")!.QuestionState, "resubmitting clears the rejection");
    }

    [TestMethod]
    public void McpQuestion_SendFailure_ReopensTheForm()
    {
        var state = ChatReducer.Replay(
        [
            Question("q1", atSeconds: 0),
            new McpAnswerSubmitted("q1") { Timestamp = At(1) },
            new McpAnswerFailed("q1", "the answer couldn't be sent (bus down)") { Timestamp = At(2) },
        ]);

        var form = state.FindQuestion("q1")!;
        Assert.AreEqual(McpQuestionState.Rejected, form.QuestionState);
        Assert.IsFalse(form.IsSubmitting);
        Assert.IsFalse(state.IsBusy(At(3)));
    }

    [TestMethod]
    public void McpQuestion_ClosedFormStaysClosed()
    {
        var closed = ChatReducer.Replay(
        [
            Question("q1", atSeconds: 0),
            QuestionStatus("q1", McpQuestionState.Expired, atSeconds: 1),
        ]);

        var late = ChatReducer.Apply(closed, QuestionStatus("q1", McpQuestionState.Rejected, atSeconds: 2));

        Assert.IsFalse(late.Accepted);
        Assert.AreEqual(McpQuestionState.Expired, late.State.FindQuestion("q1")!.QuestionState);
        Assert.IsFalse(ChatReducer.Apply(closed, new McpAnswerSubmitted("q1") { Timestamp = At(3) }).Accepted);
    }

    [TestMethod]
    public void McpQuestion_PastExpiry_CannotBeAnswered()
    {
        var state = ChatReducer.Reduce(ChatViewState.Empty, Question("q1", atSeconds: 0, expiresInSeconds: 60));

        Assert.IsFalse(ChatReducer.Apply(state, new McpAnswerSubmitted("q1") { Timestamp = At(61) }).Accepted);
    }

    [TestMethod]
    public void McpQuestion_UnreadableForm_ShowsPlainText()
    {
        var state = ChatReducer.Reduce(ChatViewState.Empty,
            new McpQuestionReceived(Reply("Answer in plain text, please."), Form: null) { Timestamp = At(0) });

        var msg = state.Messages.Single();
        Assert.AreEqual(MessageCategory.McpQuestion, msg.Category);
        Assert.IsNull(msg.Question);
        Assert.AreEqual("Answer in plain text, please.", msg.Content);
    }

    [TestMethod]
    public void McpQuestion_LostFollowUp_StopsCountingAsBusyAfterTheWaitLimit()
    {
        var state = ChatReducer.Replay(
        [
            Question("q1", atSeconds: 0, expiresInSeconds: 3600),
            new McpAnswerSubmitted("q1") { Timestamp = At(1) },
        ]);

        Assert.IsTrue(state.IsBusy(At(1) + ChatViewState.FormWaitLimit - TimeSpan.FromSeconds(1)));
        Assert.IsFalse(state.IsBusy(At(1) + ChatViewState.FormWaitLimit));
    }

    // ── Cancel, stale turns, reloads, clear ──────────────────────────────

    [TestMethod]
    public void UserCancelled_EndsTheTurnAndEveryFormWait()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            Question("q1", atSeconds: 1),
            new McpAnswerSubmitted("q1") { Timestamp = At(2) },
            new UserCancelled { Timestamp = At(3) },
            // The send loop reports the cancellation afterwards; there is nothing left to release.
            new TurnEnded("turn-1", TurnOutcome.Cancelled) { Timestamp = At(4) },
        ]);

        Assert.IsFalse(state.IsBusy(At(5)));
        Assert.IsFalse(state.Messages.Any(m => m.IsError), "a cancel adds no error");
    }

    [TestMethod]
    public void StaleTurnEnded_DoesNotReleaseANewerTurn()
    {
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-1") { Timestamp = At(0) },
            new UserCancelled { Timestamp = At(1) },
            new TurnStarted("turn-2") { Timestamp = At(2) },
            new TurnEnded("turn-1", TurnOutcome.TimedOut) { Timestamp = At(3) },
        ]);

        Assert.AreEqual("turn-2", state.CurrentTurn?.TurnId);
        Assert.IsTrue(state.IsBusy(At(4)));
    }

    [TestMethod]
    public void UnsolicitedPrimaryReply_EndsATurnWhoseViewWentAway()
    {
        // The view that started the turn was lost (reload, SignalR reconnect), so its TurnEnded
        // never comes. The primary agent's final reply still ends the turn.
        var state = ChatReducer.Replay(
        [
            new TurnStarted("turn-from-lost-view") { Timestamp = At(0) },
            new ReplyReceived(Reply("Done."), MessageCategory.PrimaryFinal) { Timestamp = At(5) },
        ]);

        Assert.IsNull(state.CurrentTurn);
        Assert.IsFalse(state.IsBusy(At(6)));
    }

    [TestMethod]
    public void TurnStarted_ClosesLogsLeftOpenAndClearsThinking()
    {
        var state = ChatReducer.Replay(
        [
            new TurnProgress("turn-0", "orphaned progress") { Timestamp = At(0), MessageId = "orphan" },
            new TurnStarted("turn-1") { Timestamp = At(1) },
        ]);

        Assert.IsFalse(state.IsActivityLogOpen("orphan"));
        Assert.IsNull(state.ThinkingMessage);
    }

    [TestMethod]
    public void HistoryLoaded_RebuildsTranscript_KeepsOpenQuestions_ResetsLogs()
    {
        var before = ChatReducer.Replay(
        [
            new AgentInfoReceived("DockerBot", "2.0") { Timestamp = At(0) },
            new UserMessageSent("stale", "u1", Session) { Timestamp = At(1) },
            new TurnProgress("turn-1", "working") { Timestamp = At(2), MessageId = "log" },
            Question("open", atSeconds: 15),
            Question("closed", atSeconds: 16),
            QuestionStatus("closed", McpQuestionState.Answered, atSeconds: 17),
        ]);

        var loaded = ChatReducer.Reduce(before, new HistoryLoaded(
        [
            new ConversationHistoryTurn { Role = "system", Content = "consolidation", Timestamp = At(5) },
            new ConversationHistoryTurn { Role = "user", Content = "hello", Timestamp = At(10) },
            new ConversationHistoryTurn { Role = "assistant", Content = "hi", Timestamp = At(11), AgentName = "RockBot" },
            new ConversationHistoryTurn { Role = "user", Content = "[Subagent done]", Timestamp = At(12), AgentName = "subagent-x" },
            new ConversationHistoryTurn { Role = "user", Content = "from peer", Timestamp = At(13), AgentName = "PeerBot" },
            new ConversationHistoryTurn { Role = "assistant", Content = "later", Timestamp = At(20) },
        ], Session) { Timestamp = At(30), IdPrefix = "h" });

        CollectionAssert.AreEqual(
            new[] { "hello", "hi", "[Subagent done]", "from peer", "Please fill in the form.", "later" },
            loaded.Messages.Select(m => m.Content).ToArray(),
            "system turns are skipped and the open question keeps its place in time");
        Assert.AreEqual("open", loaded.Messages[4].Question?.QuestionId);

        Assert.AreEqual(MessageCategory.UserInput, loaded.Messages[0].Category);
        Assert.AreEqual(MessageCategory.PrimaryFinal, loaded.Messages[1].Category);
        Assert.AreEqual("DockerBot", loaded.Messages[1].AgentName, "primary turns use the current name");
        Assert.AreEqual(MessageCategory.SubagentActivity, loaded.Messages[2].Category);
        Assert.AreEqual(MessageCategory.A2AActivity, loaded.Messages[3].Category);
        Assert.AreEqual("h-0", loaded.Messages[0].MessageId);

        Assert.AreEqual(0, loaded.OpenActivityLogs.Count);
        Assert.IsNull(loaded.FindQuestion("closed"));
    }

    [TestMethod]
    public void ConversationCleared_ResetsEverythingButAgentInfo()
    {
        var state = ChatReducer.Replay(
        [
            new AgentInfoReceived("RockBot", "1.0") { Timestamp = At(0) },
            new UserMessageSent("hi", "u1", Session) { Timestamp = At(1) },
            new TurnStarted("turn-1") { Timestamp = At(1) },
            new TurnProgress("turn-1", "working") { Timestamp = At(2) },
            Question("q1", atSeconds: 3),
            new McpAnswerSubmitted("q1") { Timestamp = At(4) },
            new ConversationCleared { Timestamp = At(5) },
        ]);

        Assert.AreEqual(0, state.Messages.Count);
        Assert.AreEqual(0, state.OpenActivityLogs.Count);
        Assert.AreEqual(0, state.FormWaits.Count);
        Assert.IsFalse(state.IsBusy(At(6)));
        Assert.AreEqual("RockBot", state.AgentName);
        Assert.AreEqual("1.0", state.AgentVersion);
    }

    // ── User actions ─────────────────────────────────────────────────────

    [TestMethod]
    public void Feedback_IsRecordedOnce()
    {
        var state = ChatReducer.Reduce(ChatViewState.Empty,
            new ReplyReceived(Reply("answer"), MessageCategory.PrimaryFinal) { Timestamp = At(0), MessageId = "r" });

        var first = ChatReducer.Apply(state, new FeedbackRecorded("r", IsPositive: true) { Timestamp = At(1) });
        var second = ChatReducer.Apply(first.State, new FeedbackRecorded("r", IsPositive: false) { Timestamp = At(2) });

        Assert.IsTrue(first.Accepted);
        Assert.IsFalse(second.Accepted);
        Assert.AreEqual(FeedbackState.ThumbsUp, second.State.FindMessage("r")!.Feedback);
    }

    [TestMethod]
    public void ExpandToggle_FlipsOnlyThatMessage()
    {
        var state = ChatReducer.Replay(
        [
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-a", "x") { Timestamp = At(0), MessageId = "a" },
            new ActivityReported(MessageCategory.SubagentActivity, "subagent-b", "y") { Timestamp = At(0), MessageId = "b" },
            new MessageExpandToggled("a") { Timestamp = At(1) },
        ]);

        Assert.IsTrue(state.FindMessage("a")!.IsExpanded);
        Assert.IsFalse(state.FindMessage("b")!.IsExpanded);
    }

    [TestMethod]
    public void SavedReference_AddedThenRemoved_TracksTheLatestOne()
    {
        var state = ChatReducer.Replay(
        [
            new SavedReferenceAdded("s1", "Plan", "plan text", "RockBot") { Timestamp = At(0) },
            new SavedReferenceAdded("s2", "Notes", "notes text", "RockBot") { Timestamp = At(1) },
        ]);

        Assert.AreEqual("s2", state.ActiveSavedReference?.SavedResponseId);
        Assert.AreEqual("Notes", state.ActiveSavedReference?.SavedResponseLabel);

        state = ChatReducer.Reduce(state, new SavedReferenceRemoved("s2") { Timestamp = At(2) });

        Assert.AreEqual("s1", state.ActiveSavedReference?.SavedResponseId);
    }
}

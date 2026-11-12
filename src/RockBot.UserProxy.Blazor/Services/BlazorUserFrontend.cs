using RockBot.Host;
using RockBot.UserProxy.ChatState;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Blazor implementation of IUserFrontend. Turns each callback into a <see cref="ChatEvent"/>
/// for the chat state; the UI renders from that state.
/// </summary>
public sealed class BlazorUserFrontend(ChatStateService chatState, McpSignInStateService? signIns = null) : IUserFrontend
{
    /// <summary>
    /// Learned from the first IsFinal=true reply. Used to distinguish primary agent
    /// progress from external A2A agent progress (both arrive as unsolicited non-final
    /// messages with a non-empty AgentName).
    /// </summary>
    private string? _primaryAgentName;

    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        // A handed-back MCP question, shown as a form. It comes from the bridge, not the
        // primary agent, so it must not teach us the agent's name; nor does it end the turn.
        if (string.Equals(reply.ContentType, McpQuestionForm.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            chatState.Apply(new McpQuestionReceived(reply, McpQuestionFormModel.TryParseForm(reply.StructuredData)));
            return Task.CompletedTask;
        }

        // Learn the primary agent name from the first final reply we see.
        if (reply.IsFinal && _primaryAgentName is null && !string.IsNullOrEmpty(reply.AgentName))
            _primaryAgentName = reply.AgentName;

        var category = CategorizeReply(reply);

        // Prepend an origin anchor (as a markdown blockquote) for unsolicited replies whose
        // work started elsewhere, so the user can re-ground a message that arrived in this
        // client after the originating session is gone. Suppressed for replies delivered on
        // their own blazor session (origin channel + session match).
        var displayReply = reply;
        var anchor = ReplyOriginFormatter.RenderAnchor(
            reply.Origin, currentChannel: "blazor", currentSessionId: reply.SessionId, DateTimeOffset.UtcNow);
        if (anchor is not null)
        {
            var quoted = string.Join("\n", anchor.Split('\n').Select(l => "> " + l.TrimStart()));
            displayReply = reply with { Content = quoted + "\n\n" + reply.Content };
        }

        // Final result — a permanent chat bubble; clears the progress indicator
        chatState.Apply(new ReplyReceived(displayReply, category));
        return Task.CompletedTask;
    }

    public Task DisplayStatusAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        var category = CategorizeReply(reply);
        var agentName = LogAgentName(reply);

        // A structured tool call: one entry per call, updated in place as it finishes.
        if (reply.ToolCall is { } toolCall && !reply.IsCompletion)
        {
            chatState.Apply(new ToolCallReported(category, agentName, toolCall, reply.Content));
            return Task.CompletedTask;
        }

        // Non-final progress — append to the source's activity log bubble
        // instead of creating a separate bubble per message.
        // IsCompletion signals the source has finished (e.g. subagent result Phase 1) —
        // close the activity log so the spinner and header indicator disappear.
        chatState.Apply(new ActivityReported(category, agentName, reply.Content, reply.IsCompletion));
        return Task.CompletedTask;
    }

    // ── Structured agent events (#795) ──────────────────────────────────────

    public Task OnSubagentStartedAsync(SubagentStarted started, CancellationToken cancellationToken = default)
    {
        chatState.Apply(new SubagentRunStarted(started));
        return Task.CompletedTask;
    }

    public Task OnSubagentFinishedAsync(SubagentFinished finished, CancellationToken cancellationToken = default)
    {
        chatState.Apply(new SubagentRunFinished(finished));
        return Task.CompletedTask;
    }

    public Task OnActiveWorkSnapshotAsync(ActiveStatusResponse snapshot, CancellationToken cancellationToken = default)
    {
        chatState.Apply(new ActiveStatusReconciled(snapshot));
        return Task.CompletedTask;
    }

    public Task OnTaskListSnapshotAsync(TaskListSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        chatState.Apply(new TaskListSnapshotReceived(snapshot));
        return Task.CompletedTask;
    }

    public Task OnTaskListDeltaAsync(TaskListDelta delta, CancellationToken cancellationToken = default)
    {
        chatState.Apply(new TaskListDeltaReceived(delta));
        return Task.CompletedTask;
    }

    // Run events for a turn this view stopped waiting on (it timed out): the agent is still at it.
    public Task OnRunStartedAsync(RunStarted run, CancellationToken cancellationToken = default)
    {
        chatState.Apply(new TurnHeartbeat(run.RunId, null));
        return Task.CompletedTask;
    }

    public Task OnRunHeartbeatAsync(RunHeartbeat heartbeat, CancellationToken cancellationToken = default)
    {
        chatState.Apply(new TurnHeartbeat(heartbeat.RunId, heartbeat.Status));
        return Task.CompletedTask;
    }

    public Task OnRunFinishedAsync(RunFinished run, CancellationToken cancellationToken = default)
    {
        chatState.Apply(new LateRunFinished(run.RunId, run.Outcome));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The agent name an activity log is kept under. A subagent's or worker's comes from its
    /// source, so its progress lands in the same log its lifecycle events open.
    /// </summary>
    private static string? LogAgentName(AgentReply reply) =>
        reply.Source is { Kind: ReplySourceKind.Subagent or ReplySourceKind.Worker, TaskId: { Length: > 0 } taskId } source
            ? ChatReducer.SubagentAgentName(source.Kind, taskId)
            : reply.AgentName;

    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        chatState.Apply(new ErrorReported(message));
        return Task.CompletedTask;
    }

    public Task OnAgentNameChangedAsync(string agentName, CancellationToken cancellationToken = default)
    {
        _primaryAgentName = agentName;
        chatState.Apply(new AgentInfoReceived(agentName, chatState.State.AgentVersion ?? ""));
        return Task.CompletedTask;
    }

    public Task OnMcpQuestionStatusAsync(McpQuestionStatus status, CancellationToken cancellationToken = default)
    {
        chatState.Apply(new McpQuestionStatusChanged(status));
        return Task.CompletedTask;
    }

    public Task OnMcpSignInStatusAsync(McpSignInStatus status, CancellationToken cancellationToken = default)
    {
        signIns?.Apply(status);
        return Task.CompletedTask;
    }

    internal MessageCategory CategorizeReply(AgentReply reply)
    {
        // Newer agents say where a reply came from; older ones leave it to the heuristics below.
        switch (reply.Source?.Kind)
        {
            case ReplySourceKind.Subagent or ReplySourceKind.Worker:
                return MessageCategory.SubagentActivity;
            case ReplySourceKind.A2A:
                // Inbound notifications are A2A activity. The A2A result handlers, though, post the
                // agent's own answer in the user's session: that is the agent's reply, and their
                // progress is told apart (the agent's own vs a remote agent's) by the checks below.
                if (reply.SessionId?.StartsWith("a2a-inbound", StringComparison.OrdinalIgnoreCase) == true)
                    return MessageCategory.A2AActivity;
                if (reply.IsFinal)
                    return MessageCategory.PrimaryFinal;
                break;
            case ReplySourceKind.Scheduled:
                // Background system jobs and the user's own scheduled tasks share the kind; the
                // session tells them apart.
                return reply.SessionId == WellKnownSessions.ScheduledSystem
                    ? MessageCategory.ScheduledSystem
                    : MessageCategory.ScheduledUser;
            case ReplySourceKind.Primary or ReplySourceKind.Bridge:
                return reply.IsFinal ? MessageCategory.PrimaryFinal : MessageCategory.PrimaryProgress;
        }

        if (reply.SessionId == WellKnownSessions.ScheduledSystem)
            return MessageCategory.ScheduledSystem;

        if (reply.SessionId == "scheduled")
            return MessageCategory.ScheduledUser;

        // Both "subagent-" and "worker-" prefixed AgentNames represent spawned activity
        // the primary agent is supervising — workers are a lean rung of the same family.
        // Worker tool-call progress (via ToolProgressNotifier with AgentName=worker-{taskId})
        // would otherwise fall through to the A2A branch since "worker-foo" != primary name.
        if (reply.AgentName?.StartsWith("subagent-", StringComparison.OrdinalIgnoreCase) == true
            || reply.AgentName?.StartsWith("worker-", StringComparison.OrdinalIgnoreCase) == true)
            return MessageCategory.SubagentActivity;

        // Inbound A2A notifications use a dedicated session ID regardless of IsFinal
        if (reply.SessionId?.StartsWith("a2a-inbound", StringComparison.OrdinalIgnoreCase) == true)
            return MessageCategory.A2AActivity;

        if (reply.IsFinal)
            return MessageCategory.PrimaryFinal;

        // Non-final with an agent name: A2A only if we know the primary agent name AND
        // the reply is from a different agent. Before the first final reply arrives we
        // haven't learned the primary name yet — default to PrimaryProgress in that case
        // rather than mislabelling the primary agent's own tool progress as A2A.
        if (_primaryAgentName is not null &&
            !string.IsNullOrEmpty(reply.AgentName) &&
            !string.Equals(reply.AgentName, _primaryAgentName, StringComparison.OrdinalIgnoreCase))
            return MessageCategory.A2AActivity;

        return MessageCategory.PrimaryProgress;
    }
}

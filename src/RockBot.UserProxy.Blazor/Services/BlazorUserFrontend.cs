using RockBot.Host;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Blazor implementation of IUserFrontend that updates chat state for real-time UI updates.
/// </summary>
public sealed class BlazorUserFrontend(ChatStateService chatState) : IUserFrontend
{
    /// <summary>
    /// Learned from the first IsFinal=true reply. Used to distinguish primary agent
    /// progress from external A2A agent progress (both arrive as unsolicited non-final
    /// messages with a non-empty AgentName).
    /// </summary>
    private string? _primaryAgentName;

    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
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

        // Final result — add as a permanent chat bubble and clear the progress indicator
        chatState.SetThinkingMessage(null);
        chatState.AddAgentReply(displayReply, category);
        return Task.CompletedTask;
    }

    public Task DisplayStatusAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        var category = CategorizeReply(reply);

        // Non-final progress — append to the source's activity log bubble
        // instead of creating a separate bubble per message.
        // IsCompletion signals the source has finished (e.g. subagent result Phase 1) —
        // close the activity log so the spinner and header indicator disappear.
        chatState.AppendActivityLogEntry(reply.Content, category, reply.AgentName,
            close: reply.IsCompletion);
        return Task.CompletedTask;
    }

    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        chatState.AddError(message);
        return Task.CompletedTask;
    }

    public Task OnAgentNameChangedAsync(string agentName, CancellationToken cancellationToken = default)
    {
        _primaryAgentName = agentName;
        chatState.SetAgentInfo(agentName, chatState.AgentVersion ?? "");
        return Task.CompletedTask;
    }

    private MessageCategory CategorizeReply(AgentReply reply)
    {
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

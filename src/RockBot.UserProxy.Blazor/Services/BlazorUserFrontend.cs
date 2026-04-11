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

        if (reply.IsFinal)
        {
            // Final result — add as a permanent chat bubble and clear the progress indicator
            chatState.SetThinkingMessage(null);
            chatState.AddAgentReply(reply, category);
        }
        else
        {
            // Non-final progress — append to the source's activity log bubble
            // instead of creating a separate bubble per message.
            // IsCompletion signals the source has finished (e.g. subagent result Phase 1) —
            // close the activity log so the spinner and header indicator disappear.
            chatState.AppendActivityLogEntry(reply.Content, category, reply.AgentName,
                close: reply.IsCompletion);
        }
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
        if (reply.SessionId == "scheduled-system")
            return MessageCategory.ScheduledSystem;

        if (reply.SessionId == "scheduled")
            return MessageCategory.ScheduledUser;

        if (reply.AgentName?.StartsWith("subagent-", StringComparison.OrdinalIgnoreCase) == true)
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

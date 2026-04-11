using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.A2A;
using RockBot.Host;
using RockBot.Memory;

namespace RockBot.Agent.A2A;

/// <summary>
/// Handles inbound A2A task requests directed at RockBot. Dispatches by trust level:
/// <list type="bullet">
/// <item>Level 1 (Observe): read-only LLM pass, writes summary to working memory, notifies user</item>
/// <item>Level 4 (Act): executes approved skills autonomously (notify-user, query-availability)</item>
/// </list>
/// </summary>
internal sealed class RockBotTaskHandler(
    AgentLoopRunner agentLoopRunner,
    IWorkingMemory workingMemory,
    MemoryTools memoryTools,
    IAgentTrustStore trustStore,
    IInboundNotificationQueue notificationQueue,
    IUserActivityMonitor userActivityMonitor,
    ISessionTracker sessionTracker,
    IConversationMemory conversationMemory,
    ILogger<RockBotTaskHandler> logger) : IAgentTaskHandler
{
    private const string ObserveSystemPrompt =
        """
        An external agent has sent you a task request. You are evaluating it on behalf of your user.

        Your job:
        1. Analyze the request — what is the caller asking for?
        2. Check your long-term memories (SearchMemory) for any relevant context about the caller or topic.
        3. Write a concise summary and suggested action to working memory so the user can review it.

        You MUST save the following to working memory:
        - Key "summary": A brief summary of what the caller wants and your recommended action for the user.

        Do NOT take any actions, make commitments, or respond on behalf of the user.
        Do NOT write to long-term memory.
        You are strictly in observation mode.
        """;

    /// <summary>Default session used to check idle state.</summary>
    private static readonly string PrimarySessionId = WellKnownSessions.Primary;

    public async Task<AgentTaskResult> HandleTaskAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;

        // Extract verified identity (placed by IdentityVerificationMiddleware)
        VerifiedAgentIdentity identity;
        if (context.MessageContext.Items.TryGetValue(
                VerifiedAgentIdentity.ContextKey, out var obj) && obj is VerifiedAgentIdentity vid)
        {
            identity = vid;
        }
        else
        {
            logger.LogWarning(
                "No verified identity found for inbound A2A task {TaskId} — using fallback from Source '{Source}'",
                request.TaskId, context.MessageContext.Envelope.Source);
            identity = new VerifiedAgentIdentity
            {
                AgentId = context.MessageContext.Envelope.Source ?? "unknown",
                DisplayName = context.MessageContext.Envelope.Source ?? "unknown",
                Issuer = "fallback",
                IsSelfAsserted = true
            };
        }

        logger.LogInformation(
            "Inbound A2A task {TaskId} from {CallerId} (skill={Skill}, self-asserted={SelfAsserted})",
            request.TaskId, identity.AgentId, request.Skill, identity.IsSelfAsserted);

        // Update trust tracking
        var trust = await trustStore.GetOrCreateAsync(identity.AgentId, ct);
        trust = trust with
        {
            LastInteraction = DateTimeOffset.UtcNow,
            InteractionCount = trust.InteractionCount + 1
        };
        await trustStore.UpdateAsync(trust, ct);

        // Dispatch built-in skills for Level 4 callers with approved skills
        if (trust.Level >= AgentTrustLevel.Act &&
            trust.ApprovedSkills.Contains(request.Skill, StringComparer.OrdinalIgnoreCase))
        {
            return request.Skill.ToLowerInvariant() switch
            {
                "notify-user" => await HandleNotifyUserAsync(request, identity, ct),
                "query-availability" => HandleQueryAvailability(request),
                "negotiate-meeting" => await HandleNegotiateMeetingAsync(request, identity, context, ct),
                _ => await HandleObserveAsync(request, identity, context, ct)
            };
        }

        // Default: Level 1 (Observe) behavior for all callers
        return await HandleObserveAsync(request, identity, context, ct);
    }

    private async Task<AgentTaskResult> HandleObserveAsync(
        AgentTaskRequest request,
        VerifiedAgentIdentity caller,
        AgentTaskContext context,
        CancellationToken ct)
    {
        // Publish Working status
        await context.PublishStatus(new AgentTaskStatusUpdate
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Working,
            Message = new AgentMessage
            {
                Role = "agent",
                Parts = [new AgentMessagePart { Kind = "text", Text = "Reviewing your request..." }]
            }
        }, ct);

        var question = ExtractText(request);

        // Use contextId for session continuity if provided, otherwise use taskId.
        // This allows multi-turn conversations to maintain LLM context across rounds.
        var sessionId = !string.IsNullOrEmpty(request.ContextId)
            ? $"a2a-inbound/{request.ContextId}"
            : $"a2a-inbound/{request.TaskId}";

        // Check if this is a continuation of an existing conversation
        var existingTurns = await conversationMemory.GetTurnsAsync(sessionId, ct);
        var isContinuation = existingTurns.Count > 0;

        if (isContinuation)
        {
            logger.LogInformation(
                "A2A inbound continuation for contextId={ContextId} (existing turns={Count})",
                request.ContextId, existingTurns.Count);
        }

        // Build restricted tool set
        var tools = InboundA2AToolSet.Build(workingMemory, memoryTools, request.TaskId, logger);
        var chatOptions = new ChatOptions { Tools = [.. tools] };

        List<ChatMessage> chatMessages;
        if (isContinuation)
        {
            // Rebuild from conversation history + new follow-up turn
            chatMessages = [new(ChatRole.System, ObserveSystemPrompt)];
            foreach (var turn in existingTurns)
            {
                var role = turn.Role == "assistant" ? ChatRole.Assistant : ChatRole.User;
                chatMessages.Add(new ChatMessage(role, turn.Content));
            }
            chatMessages.Add(new(ChatRole.User,
                $"Follow-up from agent '{caller.DisplayName}' (ID: {caller.AgentId}):\n{question}"));
        }
        else
        {
            chatMessages =
            [
                new(ChatRole.System, ObserveSystemPrompt),
                new(ChatRole.User,
                    $"Inbound request from agent '{caller.DisplayName}' (ID: {caller.AgentId}, " +
                    $"self-asserted: {caller.IsSelfAsserted}).\n" +
                    $"Skill requested: {request.Skill}\n\n" +
                    $"Message:\n{question}")
            ];
        }

        // Run read-only LLM pass
        var summary = await agentLoopRunner.RunAsync(
            chatMessages, chatOptions, sessionId,
            tier: ModelTier.Low,
            enableFollowUp: false,
            enableCompletionEval: false,
            cancellationToken: ct);

        // Store turns for future continuation
        await conversationMemory.AddTurnAsync(sessionId,
            new ConversationTurn("user", question, DateTimeOffset.UtcNow)
            { AgentName = caller.DisplayName },
            ct);
        await conversationMemory.AddTurnAsync(sessionId,
            new ConversationTurn("assistant", summary, DateTimeOffset.UtcNow),
            ct);

        // Ensure caller info is in working memory
        await workingMemory.SetAsync(
            $"a2a-inbox/{request.TaskId}/caller",
            $"Agent: {caller.DisplayName} (ID: {caller.AgentId}, issuer: {caller.Issuer ?? "unknown"}, " +
            $"self-asserted: {caller.IsSelfAsserted})",
            TimeSpan.FromHours(24), "a2a", ["inbound", "caller"]);

        await workingMemory.SetAsync(
            $"a2a-inbox/{request.TaskId}/request",
            $"Skill: {request.Skill}\n\n{question}",
            TimeSpan.FromHours(24), "a2a", ["inbound", "request"]);

        await workingMemory.SetAsync(
            $"a2a-inbox/{request.TaskId}/status",
            isContinuation ? "follow-up-processed" : "pending-review",
            TimeSpan.FromHours(24), "a2a", ["inbound", "status"]);

        logger.LogInformation(
            "A2A task {TaskId} from {CallerId} processed at Observe level (continuation={IsContinuation}), summary length={Len}",
            request.TaskId, caller.AgentId, isContinuation, summary.Length);

        // Queue notification for the user (batched delivery when idle)
        await notificationQueue.EnqueueAsync(new InboundNotification
        {
            TaskId = request.TaskId,
            CallerName = caller.DisplayName,
            Summary = summary,
            ReceivedAt = DateTimeOffset.UtcNow,
            SkillId = request.Skill
        }, ct);

        return new AgentTaskResult
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Completed,
            Message = new AgentMessage
            {
                Role = "agent",
                Parts = [new AgentMessagePart
                {
                    Kind = "text",
                    Text = "Your request has been received and the user will be notified. " +
                           "A summary and suggested action have been prepared for their review."
                }]
            }
        };
    }

    private async Task<AgentTaskResult> HandleNotifyUserAsync(
        AgentTaskRequest request,
        VerifiedAgentIdentity caller,
        CancellationToken ct)
    {
        var message = ExtractText(request);

        // Write notification to A2A inbox
        await workingMemory.SetAsync(
            $"a2a-inbox/{request.TaskId}/summary",
            $"Notification from {caller.DisplayName}: {message}",
            TimeSpan.FromHours(24), "a2a", ["inbound", "notification"]);

        await workingMemory.SetAsync(
            $"a2a-inbox/{request.TaskId}/status",
            "notification-delivered",
            TimeSpan.FromHours(24), "a2a", ["inbound", "status"]);

        logger.LogInformation("A2A notify-user from {CallerId}: {Preview}",
            caller.AgentId, message.Length > 100 ? message[..100] + "..." : message);

        await notificationQueue.EnqueueAsync(new InboundNotification
        {
            TaskId = request.TaskId,
            CallerName = caller.DisplayName,
            Summary = $"Notification: {message}",
            ReceivedAt = DateTimeOffset.UtcNow,
            SkillId = "notify-user"
        }, ct);

        return new AgentTaskResult
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Completed,
            Message = new AgentMessage
            {
                Role = "agent",
                Parts = [new AgentMessagePart { Kind = "text", Text = "User has been notified." }]
            }
        };
    }

    private AgentTaskResult HandleQueryAvailability(AgentTaskRequest request)
    {
        var hasActiveLoop = sessionTracker.HasActiveUserLoop(PrimarySessionId);
        var isRecentlyActive = userActivityMonitor.IsUserActive(TimeSpan.FromMinutes(5));

        var status = (hasActiveLoop, isRecentlyActive) switch
        {
            (true, _) => "busy",
            (false, true) => "available, may be delayed",
            (false, false) => "away"
        };

        logger.LogInformation("A2A query-availability: {Status} (activeLoop={Active}, recentActivity={Recent})",
            status, hasActiveLoop, isRecentlyActive);

        return new AgentTaskResult
        {
            TaskId = request.TaskId,
            ContextId = request.ContextId,
            State = AgentTaskState.Completed,
            Message = new AgentMessage
            {
                Role = "agent",
                Parts = [new AgentMessagePart { Kind = "text", Text = status }]
            }
        };
    }

    /// <summary>
    /// Multi-turn meeting negotiation skill. On the first call, returns
    /// <see cref="AgentTaskState.InputRequired"/> asking for preferred times.
    /// On follow-up (same contextId with existing conversation turns), confirms
    /// the meeting and returns <see cref="AgentTaskState.Completed"/>.
    /// </summary>
    private async Task<AgentTaskResult> HandleNegotiateMeetingAsync(
        AgentTaskRequest request,
        VerifiedAgentIdentity caller,
        AgentTaskContext context,
        CancellationToken ct)
    {
        var message = ExtractText(request);

        // Use contextId for multi-turn state tracking
        var contextId = request.ContextId ?? request.TaskId;
        var sessionId = $"a2a-inbound/{contextId}";

        // Check if this is a follow-up in an existing negotiation
        var existingTurns = await conversationMemory.GetTurnsAsync(sessionId, ct);
        var isContinuation = existingTurns.Count > 0;

        // Store the turn for future reference
        await conversationMemory.AddTurnAsync(sessionId,
            new ConversationTurn("user", message, DateTimeOffset.UtcNow)
            { AgentName = caller.DisplayName },
            ct);

        if (!isContinuation)
        {
            // First call — ask for preferred times
            logger.LogInformation(
                "A2A negotiate-meeting from {CallerId}: initial request, returning InputRequired",
                caller.AgentId);

            await context.PublishStatus(new AgentTaskStatusUpdate
            {
                TaskId = request.TaskId,
                ContextId = contextId,
                State = AgentTaskState.Working,
                Message = new AgentMessage
                {
                    Role = "agent",
                    Parts = [new AgentMessagePart { Kind = "text", Text = "Checking availability..." }]
                }
            }, ct);

            var questionText =
                $"I'd like to help coordinate this meeting. " +
                $"My user is available tomorrow at 10:00 AM, 2:00 PM, or 4:00 PM. " +
                $"Which of these times works for your user?";

            await conversationMemory.AddTurnAsync(sessionId,
                new ConversationTurn("assistant", questionText, DateTimeOffset.UtcNow),
                ct);

            return new AgentTaskResult
            {
                TaskId = request.TaskId,
                ContextId = contextId,
                State = AgentTaskState.InputRequired,
                Message = new AgentMessage
                {
                    Role = "agent",
                    Parts = [new AgentMessagePart { Kind = "text", Text = questionText }]
                }
            };
        }

        // Follow-up — confirm the meeting
        logger.LogInformation(
            "A2A negotiate-meeting from {CallerId}: follow-up received, confirming",
            caller.AgentId);

        var confirmationText =
            $"Meeting confirmed. I've noted the preference from your response: \"{message}\". " +
            $"My user will be notified about the scheduled meeting.";

        await conversationMemory.AddTurnAsync(sessionId,
            new ConversationTurn("assistant", confirmationText, DateTimeOffset.UtcNow),
            ct);

        // Notify our user about the confirmed meeting
        await notificationQueue.EnqueueAsync(new InboundNotification
        {
            TaskId = request.TaskId,
            CallerName = caller.DisplayName,
            Summary = $"Meeting negotiated with {caller.DisplayName}: {message}",
            ReceivedAt = DateTimeOffset.UtcNow,
            SkillId = "negotiate-meeting"
        }, ct);

        return new AgentTaskResult
        {
            TaskId = request.TaskId,
            ContextId = contextId,
            State = AgentTaskState.Completed,
            Message = new AgentMessage
            {
                Role = "agent",
                Parts = [new AgentMessagePart { Kind = "text", Text = confirmationText }]
            }
        };
    }

    private static string ExtractText(AgentTaskRequest request) =>
        request.Message.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
        ?? "(no message provided)";
}

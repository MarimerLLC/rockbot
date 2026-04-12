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

        // Dispatch built-in skills for Level 4 callers with approved skills.
        // Fuzzy-match the requested skill ID — callers may paraphrase
        // (e.g. "schedule-meeting" instead of "negotiate-meeting").
        var matchedSkill = InboundSkillMatcher.Match(request.Skill);
        if (matchedSkill is not null &&
            trust.Level >= AgentTrustLevel.Act &&
            trust.ApprovedSkills.Contains(matchedSkill, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Skill match: requested '{Requested}' → matched '{Matched}'",
                request.Skill, matchedSkill);

            return matchedSkill switch
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

        // Persist a searchable outcome so the agent can recall this interaction later.
        // Uses a stable key pattern under a2a-outcomes/ with long TTL.
        var outcomeKey = $"session/{WellKnownSessions.Primary}/a2a-outcomes/{request.Skill}/{request.ContextId ?? request.TaskId}";
        await workingMemory.SetAsync(
            outcomeKey,
            $"Inbound A2A task from {caller.DisplayName} (skill: {request.Skill}) on {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm UTC}.\n" +
            $"Request: {question}\n" +
            $"Summary: {summary}",
            ttl: TimeSpan.FromHours(8),
            category: "a2a-outcome",
            tags: [caller.DisplayName, request.Skill]);

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

        // Return a clear "not fulfilled" response so the caller's LLM does not
        // hallucinate success. The Observe path only summarises and notifies — it
        // does NOT execute the requested action.
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
                    Text = "IMPORTANT: This request was NOT completed. " +
                           "It has been forwarded to my user for manual review. " +
                           "No action has been taken and nothing has been scheduled, confirmed, or executed. " +
                           "The user may follow up separately if they choose to act on it. " +
                           "You should inform your user that the request is pending the other party's review."
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

        // Persist notification so the agent can recall it later
        await workingMemory.SetAsync(
            $"session/{WellKnownSessions.Primary}/a2a-outcomes/notify-user/{request.TaskId}",
            $"Notification from {caller.DisplayName} on {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm UTC}:\n{message}",
            ttl: TimeSpan.FromHours(8),
            category: "a2a-outcome",
            tags: [caller.DisplayName, "notification"]);

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
    /// Multi-turn meeting negotiation skill. Gathers all meeting details from
    /// the caller via <see cref="AgentTaskState.InputRequired"/> rounds before
    /// confirming and notifying the user. Questions about purpose, duration, etc.
    /// are directed at the <b>caller</b> — the user is only notified with the
    /// final confirmed details.
    ///
    /// Round 1 (initial request): ask for preferred time, purpose, and duration.
    /// Round 2 (follow-up): confirm with all details gathered.
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

        // Check conversation history to determine which round we're in
        var existingTurns = await conversationMemory.GetTurnsAsync(sessionId, ct);
        // Each round stores 2 turns (user + assistant), so turn count / 2 = completed rounds
        var completedRounds = existingTurns.Count / 2;

        // Store the caller's message
        await conversationMemory.AddTurnAsync(sessionId,
            new ConversationTurn("user", message, DateTimeOffset.UtcNow)
            { AgentName = caller.DisplayName },
            ct);

        if (completedRounds == 0)
        {
            // Round 1 — gather all details from the caller
            logger.LogInformation(
                "A2A negotiate-meeting from {CallerId}: initial request, asking for details",
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
                "I'd like to help coordinate this meeting. " +
                "My user is available tomorrow at 10:00 AM, 2:00 PM, or 4:00 PM. " +
                "Please provide the following so I can finalize:\n" +
                "1. Which of these times works for your user?\n" +
                "2. What is the purpose/topic of the meeting?\n" +
                "3. How long should it be (e.g., 30 minutes, 1 hour)?";

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

        // Round 2+ — caller provided details, confirm and complete
        logger.LogInformation(
            "A2A negotiate-meeting from {CallerId}: details received, confirming",
            caller.AgentId);

        var confirmationText =
            $"Meeting confirmed with the details you provided. " +
            $"My user will be notified with the full meeting information.";

        await conversationMemory.AddTurnAsync(sessionId,
            new ConversationTurn("assistant", confirmationText, DateTimeOffset.UtcNow),
            ct);

        // Persist the negotiation outcome with all details from both rounds.
        var allTurns = await conversationMemory.GetTurnsAsync(sessionId, ct);
        var exchangeSummary = string.Join("\n",
            allTurns.Select(t => $"  [{t.Role}] {t.Content}"));
        var outcomeText =
            $"Meeting negotiated with {caller.DisplayName} on {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm UTC}.\n" +
            $"Details from caller: {message}\n\n" +
            $"Full exchange:\n{exchangeSummary}";

        await workingMemory.SetAsync(
            $"session/{WellKnownSessions.Primary}/a2a-outcomes/negotiate-meeting/{contextId}",
            outcomeText,
            ttl: TimeSpan.FromHours(8),
            category: "a2a-outcome",
            tags: [caller.DisplayName, "meeting", "negotiation"]);

        logger.LogInformation(
            "Stored negotiate-meeting outcome for {CallerId} in working memory (8h TTL)",
            caller.AgentId);

        // Notify Bob's user with the complete meeting details — no questions,
        // just the confirmed information. All clarifications were handled with
        // the caller during the InputRequired rounds.
        await notificationQueue.EnqueueAsync(new InboundNotification
        {
            TaskId = request.TaskId,
            CallerName = caller.DisplayName,
            Summary = $"Meeting confirmed with {caller.DisplayName}: {message}",
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

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

    /// <summary>Default session used to check idle state (matches Blazor UI hardcoded session).</summary>
    private const string PrimarySessionId = "blazor-session";

    public async Task<AgentTaskResult> HandleTaskAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;

        // Extract verified identity (placed by IdentityVerificationMiddleware)
        var identity = context.MessageContext.Items.TryGetValue(
            VerifiedAgentIdentity.ContextKey, out var obj) && obj is VerifiedAgentIdentity vid
            ? vid
            : new VerifiedAgentIdentity
            {
                AgentId = context.MessageContext.Envelope.Source ?? "unknown",
                DisplayName = context.MessageContext.Envelope.Source ?? "unknown",
                Issuer = "fallback",
                IsSelfAsserted = true
            };

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
        var sessionId = $"a2a-inbound/{request.TaskId}";

        // Build restricted tool set
        var tools = InboundA2AToolSet.Build(workingMemory, memoryTools, request.TaskId, logger);

        var chatOptions = new ChatOptions { Tools = [.. tools] };
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, ObserveSystemPrompt),
            new(ChatRole.User,
                $"Inbound request from agent '{caller.DisplayName}' (ID: {caller.AgentId}, " +
                $"self-asserted: {caller.IsSelfAsserted}).\n" +
                $"Skill requested: {request.Skill}\n\n" +
                $"Message:\n{question}")
        };

        // Run read-only LLM pass
        var summary = await agentLoopRunner.RunAsync(
            chatMessages, chatOptions, sessionId,
            tier: ModelTier.Low,
            enableFollowUp: false,
            enableCompletionEval: false,
            cancellationToken: ct);

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
            "pending-review",
            TimeSpan.FromHours(24), "a2a", ["inbound", "status"]);

        logger.LogInformation(
            "A2A task {TaskId} from {CallerId} processed at Observe level, summary length={Len}",
            request.TaskId, caller.AgentId, summary.Length);

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

    private static string ExtractText(AgentTaskRequest request) =>
        request.Message.Parts
            .Where(p => p.Kind == "text")
            .Select(p => p.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
        ?? "(no message provided)";
}

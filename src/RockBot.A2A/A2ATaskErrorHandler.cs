using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.Skills;
using RockBot.Tools;
using RockBot.UserProxy;

namespace RockBot.A2A;

/// <summary>
/// Handles <see cref="AgentTaskError"/> messages from external agents.
/// Folds the error into the primary agent's LLM conversation.
/// </summary>
internal sealed class A2ATaskErrorHandler(
    AgentLoopRunner agentLoopRunner,
    AgentContextBuilder agentContextBuilder,
    ILlmClient llmClient,
    IMessagePublisher publisher,
    AgentIdentity agent,
    IWorkingMemory workingMemory,
    MemoryTools memoryTools,
    ISkillStore skillStore,
    IToolRegistry toolRegistry,
    RulesTools rulesTools,
    ToolGuideTools toolGuideTools,
    IConversationMemory conversationMemory,
    A2ATaskTracker tracker,
    AgentNameHolder agentNameHolder,
    ILogger<A2ATaskErrorHandler> logger) : IMessageHandler<AgentTaskError>
{
    private string DisplayName => agentNameHolder.DisplayName ?? agent.Name;

    /// <summary>
    /// True only when an A2A task originated from the primary agent's user session.
    /// Subagent and wisp sessions are not user-facing; their A2A errors flow back to
    /// the calling loop rather than producing user bubbles.
    /// </summary>
    private static bool IsUserSession(string primarySessionId) =>
        primarySessionId.StartsWith("session/", StringComparison.OrdinalIgnoreCase) &&
        !primarySessionId.StartsWith("session/subagent-", StringComparison.OrdinalIgnoreCase);

    public async Task HandleAsync(AgentTaskError error, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;
        var correlationId = context.Envelope.CorrelationId;

        if (string.IsNullOrWhiteSpace(correlationId) || !tracker.TryRemove(correlationId, out var pending) || pending is null)
        {
            logger.LogDebug("Received AgentTaskError with correlationId={CorrelationId} — not tracked, ignoring", correlationId);
            return;
        }

        pending.Cts.Cancel();
        pending.Cts.Dispose();

        var a2aDurationMs = (DateTimeOffset.UtcNow - pending.StartedAt).TotalMilliseconds;
        A2ADiagnostics.Failures.Add(1,
            new KeyValuePair<string, object?>("rockbot.a2a.target_agent", pending.TargetAgent));
        A2ADiagnostics.Duration.Record(a2aDurationMs,
            new KeyValuePair<string, object?>("rockbot.a2a.target_agent", pending.TargetAgent),
            new KeyValuePair<string, object?>("rockbot.a2a.status", "error"));

        logger.LogWarning(
            "A2A task error for task {TaskId} from agent '{TargetAgent}' in session {SessionId}: [{Code}] {Message}",
            error.TaskId, pending.TargetAgent, pending.PrimarySessionId, error.Code, error.Message);

        // Only the primary agent talks to the user. When the failed A2A invocation came
        // from a subagent or wisp, the calling loop will surface the failure in its own
        // output — emitting a separate user-facing bubble here would bypass the primary
        // agent and show transport-layer noise to the user.
        if (!IsUserSession(pending.PrimarySessionId))
        {
            logger.LogInformation(
                "A2A task error for {TaskId} originated from non-user session {SessionId} — " +
                "skipping synthesis and bubble publish (caller will surface the error)",
                error.TaskId, pending.PrimarySessionId);
            return;
        }

        // PrimarySessionId is the full WM session namespace (e.g. "session/blazor-session").
        // Strip the prefix for conversation memory and context builder consistency.
        var sessionNamespace = pending.PrimarySessionId;
        const string SessionPrefix = "session/";
        var rawSessionId = sessionNamespace.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
            ? sessionNamespace[SessionPrefix.Length..]
            : sessionNamespace;

        var syntheticUserTurn = $"[Agent '{pending.TargetAgent}' failed task {error.TaskId} (code={error.Code})]: {error.Message}";

        await conversationMemory.AddTurnAsync(
            rawSessionId,
            new ConversationTurn("user", syntheticUserTurn, DateTimeOffset.UtcNow)
            { AgentName = pending.TargetAgent },
            ct);

        var chatMessages = await agentContextBuilder.BuildAsync(
            rawSessionId, syntheticUserTurn, ct);

        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, sessionNamespace, logger);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, rawSessionId);
        var batchId = Guid.NewGuid().ToString("N")[..12];
        var registryTools = toolRegistry.BuildAgentToolFunctions(sessionNamespace, batchId);

        var chatOptions = new ChatOptions
        {
            Tools = [..memoryTools.Tools, ..sessionWorkingMemoryTools.Tools, ..sessionSkillTools.Tools,
                     ..rulesTools.Tools, ..toolGuideTools.Tools, ..registryTools]
        };

        try
        {
            using var progressCtx = ToolProgressNotifier.SetContext(new ToolProgressContext
            {
                SessionId = rawSessionId,
                AgentName = DisplayName,
                ReplyTo = $"{UserProxyTopics.UserResponse}.{agent.Name}"
            });

            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, rawSessionId,
                enableFollowUp: false, cancellationToken: ct);

            await conversationMemory.AddTurnAsync(
                rawSessionId,
                new ConversationTurn("assistant", finalContent, DateTimeOffset.UtcNow)
                { AgentName = agent.Name },
                ct);

            var reply = new AgentReply
            {
                Content = finalContent,
                SessionId = rawSessionId,
                AgentName = DisplayName,
                IsFinal = true
            };
            var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
            await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to handle A2A task error for task {TaskId}", error.TaskId);
        }
    }
}

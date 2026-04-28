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
/// Handles <see cref="AgentTaskStatusUpdate"/> messages from external agents.
/// Filters to only updates for tasks tracked by this agent, then folds them
/// into the primary agent's LLM conversation.
/// </summary>
internal sealed class A2ATaskStatusHandler(
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
    ILogger<A2ATaskStatusHandler> logger) : IMessageHandler<AgentTaskStatusUpdate>
{
    private string DisplayName => agentNameHolder.DisplayName ?? agent.Name;
    public async Task HandleAsync(AgentTaskStatusUpdate update, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;
        var correlationId = context.Envelope.CorrelationId;

        // Only process status updates for tasks we dispatched
        if (string.IsNullOrWhiteSpace(correlationId) || !tracker.TryGet(correlationId, out var pending) || pending is null)
        {
            logger.LogDebug("Received AgentTaskStatusUpdate with correlationId={CorrelationId} — not ours, ignoring", correlationId);
            return;
        }

        var statusText = update.Message?.Parts.FirstOrDefault(p => p.Kind == "text")?.Text;
        logger.LogInformation(
            "A2A status update for task {TaskId} from '{TargetAgent}' (state={State}): {StatusText}",
            update.TaskId, pending.TargetAgent, update.State, statusText ?? "(no message)");

        // Working status updates are ephemeral progress indicators — relay them directly
        // to the user without an LLM call. Running the LLM loop for every "Still working"
        // message produces identical hallucinated filler ("delivery imminent" etc.) because
        // the model has no real context to add. Skip conversation memory too: 20 "agent
        // working" turns would pollute the LLM context when the result finally arrives.
        if (update.State == AgentTaskState.Working)
        {
            if (statusText is not null)
            {
                var progressReply = new AgentReply
                {
                    Content = statusText,
                    SessionId = pending.PrimarySessionId,
                    AgentName = pending.TargetAgent,
                    IsFinal = false
                };
                var progressEnvelope = progressReply.ToEnvelope<AgentReply>(source: agent.Name);
                await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", progressEnvelope, ct);
            }
            return;
        }

        // Non-Working status (unexpected state transitions etc.) — fold into conversation
        // so the primary agent can reason about them.
        // PrimarySessionId is the full WM session namespace (e.g. "session/blazor-session").
        // Strip the prefix for conversation memory and context builder consistency.
        var sessionNamespace = pending.PrimarySessionId;
        const string SessionPrefix = "session/";
        var rawSessionId = sessionNamespace.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
            ? sessionNamespace[SessionPrefix.Length..]
            : sessionNamespace;

        var syntheticUserTurn = statusText is not null
            ? $"[Agent '{pending.TargetAgent}' task {update.TaskId} status={update.State}]: {statusText}"
            : $"[Agent '{pending.TargetAgent}' task {update.TaskId} status={update.State}]";

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
                IsFinal = false
            };
            var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
            await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to handle A2A status update for task {TaskId}", update.TaskId);
        }
    }
}

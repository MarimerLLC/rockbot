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
    ILogger<A2ATaskStatusHandler> logger) : IMessageHandler<AgentTaskStatusUpdate>
{
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
                await publisher.PublishAsync(UserProxyTopics.UserResponse, progressEnvelope, ct);
            }
            return;
        }

        // Non-Working status (unexpected state transitions etc.) — fold into conversation
        // so the primary agent can reason about them.
        var syntheticUserTurn = statusText is not null
            ? $"[Agent '{pending.TargetAgent}' task {update.TaskId} status={update.State}]: {statusText}"
            : $"[Agent '{pending.TargetAgent}' task {update.TaskId} status={update.State}]";

        await conversationMemory.AddTurnAsync(
            pending.PrimarySessionId,
            new ConversationTurn("user", syntheticUserTurn, DateTimeOffset.UtcNow),
            ct);

        var chatMessages = await agentContextBuilder.BuildAsync(
            pending.PrimarySessionId, syntheticUserTurn, ct);

        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, pending.PrimarySessionId, logger);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, pending.PrimarySessionId);
        var registryTools = toolRegistry.GetTools()
            .Select(r => (AIFunction)new RegistryToolFunction(
                r, toolRegistry.GetExecutor(r.Name)!, pending.PrimarySessionId))
            .ToArray();

        var chatOptions = new ChatOptions
        {
            Tools = [..memoryTools.Tools, ..sessionWorkingMemoryTools.Tools, ..sessionSkillTools.Tools,
                     ..rulesTools.Tools, ..toolGuideTools.Tools, ..registryTools]
        };

        try
        {
            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, pending.PrimarySessionId, cancellationToken: ct);

            await conversationMemory.AddTurnAsync(
                pending.PrimarySessionId,
                new ConversationTurn("assistant", finalContent, DateTimeOffset.UtcNow),
                ct);

            var reply = new AgentReply
            {
                Content = finalContent,
                SessionId = pending.PrimarySessionId,
                AgentName = agent.Name,
                IsFinal = false
            };
            var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
            await publisher.PublishAsync(UserProxyTopics.UserResponse, envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to handle A2A status update for task {TaskId}", update.TaskId);
        }
    }
}

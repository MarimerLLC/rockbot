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
/// Folds a late A2A reply (one that arrived after its owning subagent exited) into a fresh
/// primary-agent turn. The payload was stashed to working memory by
/// <see cref="A2ALateReplyFolder"/>; this handler prompts the primary to read it, decide
/// whether to act, and surface it to the user with the late-arrival context made explicit.
/// </summary>
internal sealed class LateA2ANotificationHandler(
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
    AgentNameHolder agentNameHolder,
    SessionClientCapabilityStore clientCapabilityStore,
    ILogger<LateA2ANotificationHandler> logger) : IMessageHandler<LateA2ANotificationMessage>
{
    private string DisplayName => agentNameHolder.DisplayName ?? agent.Name;

    public async Task HandleAsync(LateA2ANotificationMessage message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;

        var sessionNamespace = message.PrimarySessionId;
        const string SessionPrefix = "session/";
        var rawSessionId = sessionNamespace.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
            ? sessionNamespace[SessionPrefix.Length..]
            : sessionNamespace;

        var kindSlug = message.Kind.ToString().ToLowerInvariant();
        var syntheticUserTurn =
            $"A late {kindSlug} arrived from A2A peer '{message.PeerAgent}' for your completed subagent " +
            $"'{message.SubagentName}'. The payload is in working memory at '{message.WorkingMemoryKey}'. " +
            $"Call get_from_working_memory with that key to read it, decide whether to act on it, and inform " +
            $"the user if it is relevant. Make clear this is a late result from earlier background work so the " +
            $"context is not opaque.";

        logger.LogInformation(
            "Folding late A2A {Kind} from peer '{Peer}' (subagent {SubagentTaskId}) into primary session {Session}",
            message.Kind, message.PeerAgent, message.SubagentTaskId, rawSessionId);

        await conversationMemory.AddTurnAsync(
            rawSessionId,
            new ConversationTurn("user", syntheticUserTurn, DateTimeOffset.UtcNow) { AgentName = message.PeerAgent },
            ct);

        var chatMessages = await agentContextBuilder.BuildAsync(
            rawSessionId, syntheticUserTurn, ct,
            clientCapabilities: clientCapabilityStore.Get(rawSessionId));

        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, sessionNamespace, logger);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, rawSessionId);
        var batchId = Guid.NewGuid().ToString("N")[..12];
        var registryTools = toolRegistry.BuildAgentToolFunctions(
            sessionNamespace, batchId, ToolProfiles.A2ASynthesis, logger: logger);

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
                new ConversationTurn("assistant", finalContent, DateTimeOffset.UtcNow) { AgentName = DisplayName },
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
            logger.LogError(ex,
                "Failed to fold late A2A notification for subagent {SubagentTaskId}", message.SubagentTaskId);
        }
    }
}

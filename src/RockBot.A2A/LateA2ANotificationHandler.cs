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
    SessionOriginStore originStore,
    ILogger<LateA2ANotificationHandler> logger,
    IMcpSkillSurface? mcpSkillSurface = null,
    IAgentWorkSerializer? workSerializer = null) : IMessageHandler<LateA2ANotificationMessage>
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

        var syntheticUserTurn = SyntheticTurn(message);

        logger.LogInformation(
            "Folding late A2A {Kind} from peer '{Peer}' (subagent {SubagentTaskId}) into primary session {Session}",
            message.Kind, message.PeerAgent, message.SubagentTaskId, rawSessionId);

        // Wait for any turn in progress to finish rather than run beside it: a folded watch change
        // can act on the user's instructions (#855), as McpTaskResultHandler's turns do.
        await using var slot = workSerializer is null ? null : await workSerializer.AcquireForUserAsync("late notification", ct);

        await conversationMemory.AddTurnAsync(
            rawSessionId, UserTurn(message, syntheticUserTurn, originStore.Get(rawSessionId).ToTurnOrigin()), ct);

        var chatMessages = await agentContextBuilder.BuildAsync(
            rawSessionId, syntheticUserTurn, ct,
            clientCapabilities: clientCapabilityStore.Get(rawSessionId));

        var sessionWorkingMemoryTools = new WorkingMemoryTools(workingMemory, sessionNamespace, logger);
        var sessionSkillTools = new SkillTools(skillStore, llmClient, logger, rawSessionId,
            mcpSkillSurface: mcpSkillSurface);
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
                Source = UserProxy.ReplySource.A2A,
                SessionId = rawSessionId,
                AgentName = DisplayName,
                ReplyTo = $"{UserProxyTopics.UserResponse}.{agent.Name}"
            });

            var finalContent = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, rawSessionId,
                enableFollowUp: false, cancellationToken: ct,
                // Under the scope of the work being folded back, when it carried one (#685).
                actionGate: ActionGateScope.FromRelayed(message.RunOrigin, message.UserAskedFor));

            var reply = new AgentReply
            {
                Source = UserProxy.ReplySource.A2A,
                Content = finalContent,
                SessionId = rawSessionId,
                AgentName = DisplayName,
                IsFinal = true,
                Origin = originStore.Get(rawSessionId),
                MessageId = ConversationTurn.NewId(),
            };
            await conversationMemory.AddTurnAsync(rawSessionId, ReplyTurn(message, reply), ct);
            var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
            await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Failed to fold late A2A notification for subagent {SubagentTaskId}", message.SubagentTaskId);
        }
    }

    /// <summary>
    /// The turn that tells the primary agent what arrived, followed by the sender's
    /// <see cref="LateA2ANotificationMessage.Instructions"/> when it wrote any.
    /// </summary>
    internal static string SyntheticTurn(LateA2ANotificationMessage message)
    {
        var kindSlug = message.Kind.ToString().ToLowerInvariant();

        // A background MCP task a subagent started folds back the same way; its peer is "mcp:{server}".
        var from = message.PeerAgent.StartsWith(LateA2ANotificationMessage.McpPeerPrefix, StringComparison.Ordinal)
            ? $"MCP server '{message.PeerAgent[LateA2ANotificationMessage.McpPeerPrefix.Length..]}' (a background task)"
            : $"A2A peer '{message.PeerAgent}'";
        var hasInstructions = !string.IsNullOrWhiteSpace(message.Instructions);
        var turn =
            $"A late {kindSlug} arrived from {from} for your completed subagent " +
            $"'{message.SubagentName}'. The payload is in working memory at '{message.WorkingMemoryKey}'. " +
            (hasInstructions
                // The user's instructions below say what to do; "decide whether to act" would undercut them.
                ? "Call get_from_working_memory with that key to read it, then act on it as the instructions below say. "
                : "Call get_from_working_memory with that key to read it, decide whether to act on it, and inform " +
                  "the user if it is relevant. ") +
            "Make clear this is a late result from earlier background work so the context is not opaque.";

        return hasInstructions ? turn + "\n\n" + message.Instructions : turn;
    }

    /// <summary>The folded turn as recorded in the primary session.</summary>
    internal static ConversationTurn UserTurn(LateA2ANotificationMessage message, string content, ConversationTurnOrigin? origin) =>
        new("user", content, DateTimeOffset.UtcNow)
        {
            AgentName = message.PeerAgent,
            Source = new ConversationTurnSource(UserProxy.ReplySourceKind.A2A),
            Origin = origin,
            ExcludeFromLearning = message.ExcludeFromLearning,
        };

    /// <summary>The agent's reply to the folded turn as recorded in the primary session.</summary>
    internal static ConversationTurn ReplyTurn(LateA2ANotificationMessage message, AgentReply reply) =>
        new ConversationTurn("assistant", reply.Content, DateTimeOffset.UtcNow) { AgentName = reply.AgentName }.Matching(reply)
            // A reply about a watch tends to restate the user's instructions (#853).
            with { ExcludeFromLearning = message.ExcludeFromLearning };
}

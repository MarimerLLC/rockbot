using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="ClearContextRequest"/> by clearing conversation memory
/// for the session. Long-term memory and conversation logs are preserved.
/// </summary>
internal sealed class ClearContextHandler(
    IConversationMemory conversationMemory,
    ISessionTracker sessionTracker,
    SessionClientCapabilityStore clientCapabilityStore,
    IMessagePublisher publisher,
    AgentIdentity agent,
    ILogger<ClearContextHandler> logger) : IMessageHandler<ClearContextRequest>
{
    public async Task HandleAsync(ClearContextRequest message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;

        // Cancel any in-flight work for this session
        var handle = sessionTracker.BeginSession(message.SessionId, ct);
        sessionTracker.EndSession(message.SessionId, handle.Generation);

        // Clear conversation memory (ephemeral turns only — logs are preserved)
        await conversationMemory.ClearAsync(message.SessionId, ct);

        // Drop the cached client capabilities so the next inbound UserMessage re-establishes
        // them from scratch — important if the user returns on a different client.
        clientCapabilityStore.Clear(message.SessionId);

        logger.LogInformation("Cleared conversation context for session {SessionId}", message.SessionId);

        var reply = new AgentReply
        {
            Content = "Context cleared — starting fresh.",
            SessionId = message.SessionId,
            AgentName = agent.Name,
            IsFinal = true
        };
        var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
        await publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{agent.Name}", envelope, ct);
    }
}

using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="CancelSessionRequest"/> by cancelling any in-flight work
/// for the session and publishing an acknowledgment reply.
/// </summary>
internal sealed class CancelSessionHandler(
    ISessionTracker sessionTracker,
    IMessagePublisher publisher,
    AgentIdentity agent,
    ILogger<CancelSessionHandler> logger) : IMessageHandler<CancelSessionRequest>
{
    public async Task HandleAsync(CancelSessionRequest message, MessageHandlerContext context)
    {
        var ct = context.CancellationToken;

        // BeginSession cancels the previous CancellationTokenSource for this session,
        // which stops any background tool loop, then EndSession clears it without
        // leaving a dangling active-session entry.
        var handle = sessionTracker.BeginSession(message.SessionId, ct);
        sessionTracker.EndSession(message.SessionId, handle.Generation);

        logger.LogInformation("Cancelled session {SessionId}", message.SessionId);

        var reply = new AgentReply
        {
            Content = "Cancelled.",
            SessionId = message.SessionId,
            AgentName = agent.Name,
            IsFinal = true
        };
        var envelope = reply.ToEnvelope<AgentReply>(source: agent.Name);
        await publisher.PublishAsync(UserProxyTopics.UserResponse, envelope, ct);
    }
}

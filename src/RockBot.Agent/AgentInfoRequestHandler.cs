using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="AgentInfoRequest"/> by returning agent identity metadata.
/// Lightweight and deterministic — no LLM invocation.
/// </summary>
internal sealed class AgentInfoRequestHandler(
    IMessagePublisher publisher,
    ILogger<AgentInfoRequestHandler> logger) : IMessageHandler<AgentInfoRequest>
{
    public async Task HandleAsync(AgentInfoRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("AgentInfoRequest received with no replyTo — ignoring");
            return;
        }

        var response = new AgentInfoResponse
        {
            AgentName = context.Agent.Name,
            AgentVersion = AssemblyVersion.Current
        };

        var envelope = response.ToEnvelope<AgentInfoResponse>(
            source: context.Agent.Name,
            correlationId: correlationId);

        await publisher.PublishAsync(replyTo, envelope, ct);

        logger.LogDebug("Published agent info: {Name} v{Version}", response.AgentName, response.AgentVersion);
    }
}

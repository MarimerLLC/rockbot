using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="AttachmentUploadRequest"/> by storing the supplied bytes through
/// <see cref="InboundAttachmentService"/> and replying with an <see cref="AgentAttachment"/>
/// path reference.
/// </summary>
/// <remarks>
/// This is the fallback transport. <see cref="AttachmentUploadEndpoint"/> is the preferred one —
/// it keeps multi-megabyte payloads off the broker entirely — but it requires a client that can
/// reach the agent over HTTP, which a CLI on someone's laptop generally cannot. The bus path
/// works anywhere the client already talks to the agent, at the cost of putting the bytes through
/// RabbitMQ once.
///
/// <para>Either way the write happens agent-side: frontends co-mount the shared volume read-only
/// ("the agent owns writes") and the CLI does not mount it at all, so a client cannot stage a
/// file itself.</para>
/// </remarks>
internal sealed class AttachmentUploadHandler(
    InboundAttachmentService attachments,
    IMessagePublisher publisher,
    ILogger<AttachmentUploadHandler> logger) : IMessageHandler<AttachmentUploadRequest>
{
    public async Task HandleAsync(AttachmentUploadRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("AttachmentUploadRequest received with no replyTo — ignoring");
            return;
        }

        var response = await attachments.StoreAsync(
            message.FileName, message.Mime, message.Data, message.SessionId, ct);

        if (!response.Success)
        {
            logger.LogWarning(
                "Rejected inbound attachment {FileName} ({Mime}) over the bus for session {SessionId}: {Error}",
                message.FileName, message.Mime, message.SessionId, response.Error);
        }

        var envelope = response.ToEnvelope<AttachmentUploadResponse>(
            source: context.Agent.Name,
            correlationId: context.Envelope.CorrelationId,
            replyTo: null,
            destination: null);

        await publisher.PublishAsync(replyTo, envelope, ct);
    }
}

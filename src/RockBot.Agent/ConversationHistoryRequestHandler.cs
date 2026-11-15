using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Handles <see cref="ConversationHistoryRequest"/> by reading turns from
/// <see cref="IConversationMemory"/> and publishing a <see cref="ConversationHistoryResponse"/>
/// back to the reply topic. With <see cref="ConversationHistoryRequest.IncludeBroadcast"/>, the
/// broadcast sessions' turns are merged in by timestamp.
/// </summary>
internal sealed class ConversationHistoryRequestHandler(
    IConversationMemory conversationMemory,
    IMessagePublisher publisher,
    ILogger<ConversationHistoryRequestHandler> logger) : IMessageHandler<ConversationHistoryRequest>
{
    public async Task HandleAsync(ConversationHistoryRequest message, MessageHandlerContext context)
    {
        var replyTo = context.Envelope.ReplyTo;
        var correlationId = context.Envelope.CorrelationId;
        var ct = context.CancellationToken;

        if (string.IsNullOrEmpty(replyTo))
        {
            logger.LogWarning("ConversationHistoryRequest received with no replyTo — ignoring");
            return;
        }

        logger.LogDebug("Fetching conversation history for session {SessionId}", message.SessionId);

        var response = new ConversationHistoryResponse
        {
            Turns = await ReadTurnsAsync(conversationMemory, message, ct),
        };

        var envelope = response.ToEnvelope<ConversationHistoryResponse>(
            source: context.Agent.Name,
            correlationId: correlationId,
            replyTo: null,
            destination: null);

        await publisher.PublishAsync(replyTo, envelope, ct);

        logger.LogDebug("Published {TurnCount} history turns for session {SessionId}",
            response.Turns.Count, message.SessionId);
    }

    /// <summary>
    /// The requested session's turns, and, when asked, the broadcast sessions' turns, in timestamp
    /// order. The sort is stable and the requested session's turns come first, so its own order is
    /// kept and it wins ties. A broadcast session asking for itself gets only its own turns.
    /// </summary>
    internal static async Task<IReadOnlyList<ConversationHistoryTurn>> ReadTurnsAsync(
        IConversationMemory conversationMemory, ConversationHistoryRequest request, CancellationToken ct)
    {
        var turns = (await conversationMemory.GetTurnsAsync(request.SessionId, ct))
            .Select(t => ToHistoryTurn(t, request.SessionId))
            .ToList();

        if (!request.IncludeBroadcast || WellKnownSessions.IsBroadcast(request.SessionId))
            return turns;

        var merged = new List<ConversationHistoryTurn>(turns);
        foreach (var session in WellKnownSessions.Broadcast)
            merged.AddRange((await conversationMemory.GetTurnsAsync(session, ct)).Select(t => ToHistoryTurn(t, session)));

        // A sort on the timestamp alone, but stable: OrderBy is, and the requested session's turns
        // were added first.
        return merged.Count == turns.Count ? turns : [.. merged.OrderBy(t => t.Timestamp)];
    }

    private static ConversationHistoryTurn ToHistoryTurn(ConversationTurn t, string sessionId) => new()
    {
        Role = t.Role,
        Content = t.Content,
        Timestamp = t.Timestamp,
        AgentName = t.AgentName,
        Attachments = t.Attachments is { Count: > 0 }
            ? [.. t.Attachments.Select(a => new AgentAttachment
            {
                Mime = a.Mime,
                Path = a.Path,
                FileName = a.FileName,
            })]
            : null,
        Id = t.Id,
        SessionId = sessionId,
        Origin = t.Origin.ToReplyOrigin(),
        Source = t.Source.ToReplySource(),
    };
}

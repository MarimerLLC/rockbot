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
    ILogger<ConversationHistoryRequestHandler> logger,
    SessionClearTimes? clearTimes = null) : IMessageHandler<ConversationHistoryRequest>
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
            Turns = await ReadTurnsAsync(conversationMemory, message, ct, clearTimes?.Get(message.SessionId)),
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
    /// kept and it wins ties. A broadcast session asking for itself gets only its own turns. Broadcast
    /// turns from before <paramref name="clearedAt"/>, when the session's context was last cleared,
    /// are left out: the clear can't delete them, since every thread shares them.
    /// </summary>
    internal static async Task<IReadOnlyList<ConversationHistoryTurn>> ReadTurnsAsync(
        IConversationMemory conversationMemory, ConversationHistoryRequest request, CancellationToken ct,
        DateTimeOffset? clearedAt = null)
    {
        var includeBroadcast = request.IncludeBroadcast && !WellKnownSessions.IsBroadcast(request.SessionId);

        // #841: a copy of a broadcast session's reply, imported because the user replied to it, is
        // left out when that session's turns are merged in: the original is already there.
        var turns = (await conversationMemory.GetTurnsAsync(request.SessionId, ct))
            .Where(t => !(includeBroadcast && t.ImportedFrom is { } from && WellKnownSessions.IsBroadcast(from.SessionId)))
            .Select(t => ToHistoryTurn(t, request.SessionId))
            .ToList();

        if (!includeBroadcast)
            return turns;

        // The fixed broadcast sessions, and any a2a-inbound/… ones, which are broadcast too.
        var sessions = WellKnownSessions.Broadcast.Concat(
            (await conversationMemory.ListSessionsAsync(ct)).Where(s => WellKnownSessions.IsBroadcast(s)))
            .Distinct(StringComparer.Ordinal);

        var merged = new List<ConversationHistoryTurn>(turns);
        foreach (var session in sessions)
            merged.AddRange((await conversationMemory.GetTurnsAsync(session, ct))
                .Where(t => clearedAt is null || t.Timestamp > clearedAt)
                .Select(t => ToHistoryTurn(t, session)));

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

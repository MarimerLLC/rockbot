using Microsoft.Extensions.Logging;
using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Conversions between a stored <see cref="ConversationTurn"/>'s provenance and the user-proxy
/// reply vocabulary (<see cref="ReplyOrigin"/>, <see cref="ReplySource"/>), and the helpers writers
/// use to store a turn that matches the reply they publish (#809).
/// </summary>
public static class ConversationTurnMapping
{
    public static ConversationTurnOrigin? ToTurnOrigin(this ReplyOrigin? origin) =>
        origin is null ? null : new(origin.Channel, origin.PromptSummary, origin.StartedAt, origin.SessionId);

    public static ReplyOrigin? ToReplyOrigin(this ConversationTurnOrigin? origin) =>
        origin is null ? null : new(origin.Channel, origin.PromptSummary, origin.StartedAt, origin.SessionId);

    public static ConversationTurnSource? ToTurnSource(this ReplySource? source) =>
        source is null ? null : new(source.Kind, source.TaskId);

    public static ReplySource? ToReplySource(this ConversationTurnSource? source) =>
        source is null ? null : new ReplySource { Kind = source.Kind, TaskId = source.TaskId };

    /// <summary>
    /// The turn with the id, origin and source of <paramref name="reply"/>, the reply it is stored
    /// from. Leaves the turn's own values where the reply has none.
    /// </summary>
    public static ConversationTurn Matching(this ConversationTurn turn, AgentReply reply) =>
        turn with
        {
            Id = reply.MessageId ?? turn.Id,
            Origin = reply.Origin.ToTurnOrigin() ?? turn.Origin,
            Source = reply.Source.ToTurnSource() ?? turn.Source,
        };

    /// <summary>
    /// Stores a reply published in a broadcast session (<see cref="WellKnownSessions.Broadcast"/>)
    /// as an assistant turn of that session, so a thread's history can include it. The turn takes
    /// the reply's message id, which this sets on the reply first when it has none; publish the
    /// returned reply. A reply in any other session is returned unchanged and not stored: its
    /// handler stores its own turns. A failure to store is logged and doesn't stop the reply.
    /// </summary>
    public static async Task<AgentReply> RecordBroadcastReplyAsync(
        this IConversationMemory? conversationMemory, AgentReply reply, ILogger logger, CancellationToken ct = default)
    {
        if (conversationMemory is null)
            return reply;
        if (!WellKnownSessions.IsBroadcast(reply.SessionId))
            return reply;

        if (string.IsNullOrEmpty(reply.MessageId))
            reply = reply with { MessageId = ConversationTurn.NewId() };

        var turn = new ConversationTurn("assistant", reply.Content, DateTimeOffset.UtcNow)
        {
            AgentName = reply.AgentName,
            Attachments = reply.Attachments is { Count: > 0 } attachments
                ? [.. attachments.Select(a => new ConversationTurnAttachment(a.Mime, a.Path, a.FileName))]
                : null,
        }.Matching(reply);

        try
        {
            await conversationMemory.AddTurnAsync(reply.SessionId, turn, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to store a {SessionId} reply in conversation history", reply.SessionId);
        }
        return reply;
    }
}

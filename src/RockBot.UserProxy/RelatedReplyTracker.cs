namespace RockBot.UserProxy;

/// <summary>
/// Remembers the unsolicited final replies this proxy passed to its frontend, so the next message
/// sent in a session can carry references to the ones from other sessions that the user saw there
/// (<see cref="UserMessage.RelatedReplies"/>). Without them a follow-up such as "summarize that",
/// typed in a client that showed another session's subagent result, reaches an agent session that
/// has never seen the result.
/// </summary>
/// <remarks>
/// Keeps the last <see cref="Capacity"/> replies, numbered in arrival order, and a cursor per
/// sending session: the number of the newest reply when that session last sent. A send takes the
/// replies after its session's cursor (<see cref="Stamp"/>) and, once the message is published,
/// moves the cursor on (<see cref="Commit"/>), so each reply is attached once per session and a
/// send that fails leaves them for the retry. A session that hasn't sent for a while still gets
/// what arrived meanwhile; one that has never sent only gets replies from the last
/// <see cref="FirstSendWindow"/>, so a new conversation doesn't pick up old reports.
/// </remarks>
internal sealed class RelatedReplyTracker(TimeProvider? timeProvider = null)
{
    /// <summary>How many recent replies are remembered.</summary>
    internal const int Capacity = 50;

    /// <summary>At most this many references go on one message, counting any the caller set.</summary>
    internal const int MaxReferences = 5;

    /// <summary>How far back a session's first message looks.</summary>
    internal static readonly TimeSpan FirstSendWindow = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Queue<(long Seq, DateTimeOffset At, AgentReply Reply)> _recent = new();
    private readonly Dictionary<string, long> _cursors = new(StringComparer.Ordinal);
    private long _seq;

    /// <summary>
    /// Remembers <paramref name="reply"/> if a later message could refer to it: a final reply the
    /// agent stored (it has a <see cref="AgentReply.MessageId"/>), and not an MCP question's form.
    /// </summary>
    public void Record(AgentReply reply)
    {
        if (!reply.IsFinal || string.IsNullOrEmpty(reply.MessageId) || string.IsNullOrEmpty(reply.SessionId))
            return;
        if (string.Equals(reply.ContentType, McpQuestionForm.ContentType, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_lock)
        {
            _recent.Enqueue((++_seq, _time.GetUtcNow(), reply));
            while (_recent.Count > Capacity)
                _recent.Dequeue();
        }
    }

    /// <summary>
    /// <paramref name="message"/> with references to the replies from other sessions that arrived
    /// since its session last sent and that <paramref name="shows"/> says were visible in it: after
    /// any the caller already set, the newest that fit in <see cref="MaxReferences"/>; and the position to
    /// pass to <see cref="Commit"/> once the message is sent. Doesn't move the cursor.
    /// </summary>
    public (UserMessage Message, long Position) Stamp(UserMessage message, Func<string, AgentReply, bool> shows)
    {
        var sessionId = message.SessionId;
        List<AgentReply> candidates;
        long position;
        lock (_lock)
        {
            position = _seq;
            if (_cursors.TryGetValue(sessionId, out var cursor))
            {
                candidates = [.. _recent.Where(r => r.Seq > cursor).Select(r => r.Reply)];
            }
            else
            {
                var since = _time.GetUtcNow() - FirstSendWindow;
                candidates = [.. _recent.Where(r => r.At >= since).Select(r => r.Reply)];
            }
        }

        // The agent imports at most MaxReferences, so that is the budget for the whole list: what
        // the caller set comes first, and the newest tracked replies fill the rest.
        var own = (message.RelatedReplies ?? []).DistinctBy(r => (r.SessionId, r.MessageId)).ToList();
        var seen = own.Select(r => (r.SessionId, r.MessageId)).ToHashSet();
        var found = new List<ReplyReference>();
        for (var i = candidates.Count - 1; i >= 0 && own.Count + found.Count < MaxReferences; i--)
        {
            var reply = candidates[i];
            if (SessionIdForms.AreSame(reply.SessionId, sessionId))
                continue;
            var reference = new ReplyReference(SessionIdForms.Raw(reply.SessionId), reply.MessageId!);
            if (!seen.Add((reference.SessionId, reference.MessageId)))
                continue;
            if (!SafeShows(shows, sessionId, reply))
                continue;
            found.Add(reference);
        }

        if (found.Count == 0)
            return (message, position);

        // Oldest first, after what the caller set.
        found.Reverse();
        return (message with { RelatedReplies = [.. own, .. found] }, position);
    }

    /// <summary>
    /// Moves <paramref name="sessionId"/>'s cursor to <paramref name="position"/> (from
    /// <see cref="Stamp"/>), after its message was published. Never moves it back.
    /// </summary>
    public void Commit(string sessionId, long position)
    {
        lock (_lock)
        {
            if (!_cursors.TryGetValue(sessionId, out var cursor) || position > cursor)
                _cursors[sessionId] = position;
        }
    }

    /// <summary>
    /// Moves <paramref name="sessionId"/>'s cursor past every reply received so far, so none of them
    /// is attached to its next message. Used when the session's context is cleared.
    /// </summary>
    public void SkipAll(string sessionId)
    {
        long position;
        lock (_lock)
            position = _seq;
        Commit(sessionId, position);
    }

    private static bool SafeShows(Func<string, AgentReply, bool> shows, string sessionId, AgentReply reply)
    {
        try
        {
            return shows(sessionId, reply);
        }
        catch
        {
            // A frontend that can't say is treated as not having shown it; the message still goes.
            return false;
        }
    }
}

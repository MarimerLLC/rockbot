namespace RockBot.UserProxy.Cli;

/// <summary>
/// The frontend for <c>rockbot chat --output jsonl</c> (#797). Broadcast and uncorrelated traffic
/// (subagent lifecycle and progress, subagent task lists, subagent results and other unsolicited
/// replies) for the turn's session goes to the <see cref="JsonLinesTurnWriter"/>; everything else
/// is dropped, so nothing reaches stdout outside the JSON-lines contract.
/// </summary>
internal sealed class JsonLinesFrontend(JsonLinesTurnWriter writer) : IUserFrontend
{
    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        if (IsThisSessionsReply(reply.SessionId))
            writer.UnsolicitedReply(reply);
        return Task.CompletedTask;
    }

    public Task DisplayStatusAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        if (!IsThisSessionsReply(reply.SessionId))
            return Task.CompletedTask;

        // A subagent's result arrives as a non-final completion (SubagentResultHandler): it is a
        // reply in its own right, not progress a later line may replace.
        if (reply.IsCompletion)
            writer.UnsolicitedReply(reply);
        else
            writer.Report(reply);
        return Task.CompletedTask;
    }

    // Errors the proxy reports outside a turn have no place in the run's stream; the run's own
    // failures end it with RUN_ERROR.
    public Task DisplayErrorAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task OnSubagentStartedAsync(SubagentStarted started, CancellationToken cancellationToken = default)
    {
        if (IsThisSession(started.ThreadId))
            writer.SubagentStarted(started);
        return Task.CompletedTask;
    }

    public Task OnSubagentFinishedAsync(SubagentFinished finished, CancellationToken cancellationToken = default)
    {
        if (IsThisSession(finished.ThreadId))
            writer.SubagentFinished(finished);
        return Task.CompletedTask;
    }

    // Only a subagent's or worker's list: the turn's own list reaches the writer as its observer,
    // and a primary list here belongs to some other turn.
    public Task OnTaskListSnapshotAsync(TaskListSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (IsThisSession(snapshot.ThreadId) && IsBackgroundWork(snapshot.Source))
            writer.OnTaskListSnapshot(snapshot);
        return Task.CompletedTask;
    }

    public Task OnTaskListDeltaAsync(TaskListDelta delta, CancellationToken cancellationToken = default)
    {
        if (IsThisSession(delta.ThreadId) && IsBackgroundWork(delta.Source))
            writer.OnTaskListDelta(delta);
        return Task.CompletedTask;
    }

    /// <summary>
    /// For events that carry a <c>ThreadId</c>: the agent always sends the raw session id there,
    /// so only an exact match is this session.
    /// </summary>
    private bool IsThisSession(string? threadId) =>
        string.Equals(threadId, writer.ThreadId, StringComparison.Ordinal);

    /// <summary>
    /// For replies: <see cref="AgentReply.SessionId"/> is the raw id, except that a subagent's or
    /// worker's tool progress keeps the agent's working-memory form, <c>session/&lt;raw id&gt;</c>.
    /// Accept exactly those two forms of this session, and strip no more than that one prefix, so
    /// a different session never matches.
    /// </summary>
    internal bool IsThisSessionsReply(string? sessionId) =>
        IsThisSession(sessionId)
        || string.Equals(sessionId, NamespacedSessionPrefix + writer.ThreadId, StringComparison.Ordinal);

    /// <summary>The prefix the agent's <c>SessionIds.Raw</c> removes.</summary>
    private const string NamespacedSessionPrefix = "session/";

    private static bool IsBackgroundWork(ReplySource source) =>
        source.Kind is ReplySourceKind.Subagent or ReplySourceKind.Worker;
}

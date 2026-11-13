namespace RockBot.UserProxy.Cli;

/// <summary>
/// The frontend for <c>rockbot chat --output jsonl</c> (#797). Broadcast and uncorrelated traffic
/// (subagent lifecycle and progress, subagent task lists, unsolicited replies) for the turn's
/// session goes to the <see cref="JsonLinesTurnWriter"/>; everything else is dropped, so nothing
/// reaches stdout outside the JSON-lines contract.
/// </summary>
internal sealed class JsonLinesFrontend(JsonLinesTurnWriter writer) : IUserFrontend
{
    public Task DisplayReplyAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        if (IsThisSession(reply.SessionId))
            writer.UnsolicitedReply(reply);
        return Task.CompletedTask;
    }

    public Task DisplayStatusAsync(AgentReply reply, CancellationToken cancellationToken = default)
    {
        if (IsThisSession(reply.SessionId))
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
    /// The agent namespaces session ids as <c>session/&lt;id&gt;</c>, and not every reply strips
    /// that (a subagent's tool progress keeps it), so compare the raw ids.
    /// </summary>
    private bool IsThisSession(string? sessionId) =>
        string.Equals(RawSessionId(sessionId), RawSessionId(writer.ThreadId), StringComparison.Ordinal);

    private const string SessionPrefix = "session/";

    internal static string? RawSessionId(string? sessionId) =>
        sessionId is not null && sessionId.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase)
            ? sessionId[SessionPrefix.Length..]
            : sessionId;

    private static bool IsBackgroundWork(ReplySource source) =>
        source.Kind is ReplySourceKind.Subagent or ReplySourceKind.Worker;
}

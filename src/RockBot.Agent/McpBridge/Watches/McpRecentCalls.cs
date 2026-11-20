namespace RockBot.Agent.McpBridge.Watches;

/// <summary>
/// The tool calls each session made recently on each server, so a watched resource's change
/// that the session's own call caused can be told apart (#853): the agent already knows what it
/// did, and a turn about it would only repeat it. In memory only: a restart forgets it, and the
/// next change is delivered as usual.
/// </summary>
/// <remarks>
/// Calls a server marks read-only (<c>readOnlyHint</c>) aren't recorded; every other call is,
/// since a server that annotates nothing gives no other way to tell.
/// </remarks>
internal sealed class McpRecentCalls(TimeProvider time)
{
    /// <summary>How long a finished call is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The longest a call counts as a change's cause after it started, however long it runs. A
    /// call parked on the user's answer or running as a background task can last hours; every
    /// change in that time isn't its echo.
    /// </summary>
    public static readonly TimeSpan MaxCauseSpan = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly List<Call> _calls = [];

    /// <summary>A call: <see cref="EndedAt"/> is null while it runs.</summary>
    internal sealed class Call
    {
        public required string SessionId { get; init; }
        public required string Server { get; init; }
        public required string Tool { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; set; }
    }

    /// <summary>Records that a call started; dispose the result (or call <see cref="End"/>) when it ends.</summary>
    public Call Start(string sessionId, string server, string tool)
    {
        var call = new Call { SessionId = sessionId, Server = server, Tool = tool, StartedAt = time.GetUtcNow() };
        lock (_gate)
        {
            Prune();
            _calls.Add(call);
        }
        return call;
    }

    /// <summary>Records that a call ended. Ending one twice keeps the first time.</summary>
    public void End(Call? call)
    {
        if (call is null)
            return;
        lock (_gate)
            call.EndedAt ??= time.GetUtcNow();
    }

    /// <summary>
    /// The latest call of <paramref name="sessionId"/> on <paramref name="server"/> that was running
    /// at <paramref name="at"/>, or had ended no more than <paramref name="grace"/> before it. A call
    /// counts for at most <see cref="MaxCauseSpan"/> after it started, ended or not.
    /// </summary>
    public Call? CauseOf(string sessionId, string server, DateTimeOffset at, TimeSpan grace)
    {
        lock (_gate)
        {
            return _calls
                .Where(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal)
                            && string.Equals(c.Server, server, StringComparison.OrdinalIgnoreCase)
                            && c.StartedAt <= at
                            && at <= Min(c.EndedAt ?? at, c.StartedAt + MaxCauseSpan) + grace)
                .OrderByDescending(c => c.StartedAt)
                .FirstOrDefault();
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private void Prune()
    {
        var cutoff = time.GetUtcNow() - Retention;
        // A call that never reported its end (a lost task) goes after twice the retention.
        _calls.RemoveAll(c => c.EndedAt is { } end ? end < cutoff : c.StartedAt < cutoff - Retention);
    }
}

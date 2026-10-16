namespace RockBot.Host;

/// <summary>
/// Whether an MCP call made in the current flow may run as a background task, whose result
/// reaches the calling session later as a new turn (<c>design/mcp-bridge.md</c>, "Long-running
/// tools"). Only a run whose session outlives the call can take that: a user conversation, or a
/// subagent (its runner waits for the task before it reports).
/// </summary>
/// <remarks>
/// Like <see cref="McpHandbackContext"/>, code that makes MCP calls on a run's behalf without
/// being that run — a wisp's deterministic steps execute inside the parent's tool call, under the
/// parent's session — must <see cref="Suppress"/> it: a wisp step needs its result now, not in a
/// later turn of its parent.
/// </remarks>
public static class McpTaskContext
{
    private const string UserSessionPrefix = "session/";
    private const string SubagentSessionPrefix = "subagent/";

    private static readonly AsyncLocal<bool> Suppressed = new();

    /// <summary>
    /// True when a call from <paramref name="sessionId"/> in the current flow can take a later
    /// result: the session is a user conversation (<c>session/{id}</c>) or a subagent
    /// (<c>subagent/{id}</c>), and the flow isn't suppressed.
    /// </summary>
    public static bool CanReceiveLater(string? sessionId) =>
        !Suppressed.Value && IsBackgroundCapableSession(sessionId);

    /// <summary>A user conversation or a subagent: the sessions a later result can reach.</summary>
    public static bool IsBackgroundCapableSession(string? sessionId) =>
        sessionId is not null
        && (sessionId.StartsWith(SubagentSessionPrefix, StringComparison.OrdinalIgnoreCase)
            || (sessionId.StartsWith(UserSessionPrefix, StringComparison.OrdinalIgnoreCase)
                && !sessionId.StartsWith(UserSessionPrefix + "subagent-", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Makes calls in the current flow synchronous until the result is disposed.</summary>
    public static IDisposable Suppress()
    {
        var previous = Suppressed.Value;
        Suppressed.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Suppressed.Value = previous;
    }
}

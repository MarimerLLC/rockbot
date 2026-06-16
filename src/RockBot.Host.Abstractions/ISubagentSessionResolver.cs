namespace RockBot.Host;

/// <summary>
/// Read-only seam letting lower-level code (e.g. <c>RockBot.A2A</c>, which does not
/// reference <c>RockBot.Subagent</c>) ask about the liveness and ownership of a subagent
/// session without taking a dependency on the subagent manager itself. Mirrors the
/// <see cref="ISessionA2AAwaiter"/> pattern.
/// </summary>
/// <remarks>
/// A subagent's A2A invocations carry its working-memory namespace as the session id
/// (e.g. <c>subagent/{taskId}</c>). When an A2A reply folds back after the owning subagent
/// has exited, the receive-side handler uses this seam to (a) confirm the session is a
/// subagent session, (b) check whether it is still active (if so, the existing awaiter path
/// handles delivery), and (c) recover the real user-facing primary session to fold into.
/// Resolution must survive the subagent's removal from the active set, so implementations
/// keep a short-lived tombstone of recently-completed subagents.
/// </remarks>
public interface ISubagentSessionResolver
{
    /// <summary>True if <paramref name="sessionId"/> names a subagent session.</summary>
    bool IsSubagentSession(string sessionId);

    /// <summary>True if the named subagent session is still running.</summary>
    bool IsActive(string sessionId);

    /// <summary>
    /// Returns the primary (user-facing) session that spawned the named subagent, or null
    /// if it cannot be resolved (unknown id, or tombstone expired).
    /// </summary>
    string? ResolvePrimarySession(string sessionId);
}

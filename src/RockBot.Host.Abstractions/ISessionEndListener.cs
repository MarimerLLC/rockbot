namespace RockBot.Host;

/// <summary>
/// Told when a run's session has ended for good: a subagent's task finished, failed or was
/// cancelled, and nothing will act in that session again. Cross-assembly seam, like
/// <see cref="ISessionA2AAwaiter"/>: subagent code doesn't reference the MCP gateway, which
/// uses this to release an MCP question handed back to the run and never answered, instead of
/// keeping its call parked until the question expires.
/// </summary>
/// <remarks>
/// Not for user conversations, which go on across turns. Implementations must be cheap and
/// must not throw for an unknown session.
/// </remarks>
public interface ISessionEndListener
{
    /// <param name="sessionId">The ended run's session, as its tool calls carried it.</param>
    /// <param name="ct">Cancellation.</param>
    Task OnSessionEndedAsync(string sessionId, CancellationToken ct);
}

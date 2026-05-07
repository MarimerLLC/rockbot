using System.Collections.Concurrent;

namespace RockBot.Host;

/// <summary>
/// In-memory map from raw session ID (e.g. "blazor-session") to the user-message
/// <c>correlationId</c> for the turn currently in flight. Populated by the entry
/// handler (e.g. <c>UserMessageHandler</c>) when it spawns work that will produce
/// the user-facing answer asynchronously, and consumed by whatever component
/// publishes the final reply (e.g. <c>SubagentResultHandler</c>'s Phase 2 synthesis).
///
/// Why: the user-proxy waits on a <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/>
/// keyed by the original correlationId for a final reply. When the parent reply is
/// demoted to non-final (because subagents will produce the actual answer), the
/// synthesis must publish with that same correlationId for the wait to resolve.
/// </summary>
public interface IPendingTurnCorrelations
{
    /// <summary>
    /// Records the correlationId for the in-flight turn on this session, replacing
    /// any prior value. Call from the entry handler before the work it spawned can
    /// produce a final reply.
    /// </summary>
    void Set(string rawSessionId, string correlationId);

    /// <summary>
    /// Atomically retrieves and removes the correlationId for this session if one
    /// was registered. Returns null when no entry exists. Single-take semantics so
    /// late or duplicate consumers don't accidentally rebind the same correlationId
    /// to a subsequent turn.
    /// </summary>
    string? TryTake(string rawSessionId);
}

internal sealed class PendingTurnCorrelations : IPendingTurnCorrelations
{
    private readonly ConcurrentDictionary<string, string> _map = new();

    public void Set(string rawSessionId, string correlationId)
    {
        _map[rawSessionId] = correlationId;
    }

    public string? TryTake(string rawSessionId)
    {
        return _map.TryRemove(rawSessionId, out var value) ? value : null;
    }
}

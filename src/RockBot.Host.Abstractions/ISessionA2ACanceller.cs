namespace RockBot.Host;

/// <summary>
/// Cancels any in-flight A2A tasks that were dispatched with the given
/// <c>PrimarySessionId</c> (e.g. a wispId). Cross-assembly seam: wisp code
/// doesn't reference RockBot.A2A directly, but a wisp that fails locally after
/// dispatching an A2A task needs to cancel the remote work so the target
/// agent doesn't run duplicate work when the LLM retries.
///
/// Implementations cancel the local <c>CancellationTokenSource</c>, drop the
/// task from the tracker (so any late result is ignored cleanly), and publish
/// an <c>AgentTaskCancelRequest</c> to the target agent's cancel topic.
/// Returns the number of tasks cancelled.
/// </summary>
public interface ISessionA2ACanceller
{
    Task<int> CancelForSessionAsync(string sessionId, string reason, CancellationToken ct);
}

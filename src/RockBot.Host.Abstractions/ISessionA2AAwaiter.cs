namespace RockBot.Host;

/// <summary>
/// Waits for any in-flight A2A tasks that were dispatched with the given
/// <c>PrimarySessionId</c> (e.g. a subagent's working-memory namespace) to reach
/// a terminal state. Cross-assembly seam: subagent code (which doesn't reference
/// RockBot.A2A) needs to block before publishing its final result so that an
/// A2A response that arrives a few seconds after the LLM loop concludes still
/// reaches the primary agent — instead of being dropped because the originating
/// subagent session no longer exists by the time A2ATaskResultHandler runs.
///
/// Implementations poll the pending-task registry and return when no tasks
/// remain for the session or when the cancellation token fires. Returns the
/// number of tasks observed pending at the start of the wait.
/// </summary>
public interface ISessionA2AAwaiter
{
    Task<int> WaitForSessionAsync(string sessionId, CancellationToken ct);
}

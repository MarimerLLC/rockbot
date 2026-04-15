namespace RockBot.Host;

/// <summary>
/// Ambient per-async-flow override for the maximum tool-calling iterations.
/// Set by <see cref="AgentLoopRunner"/> when a caller (e.g. a subagent) requests
/// more iterations than the default model behavior allows.
/// <see cref="RockBotFunctionInvokingChatClient"/> reads this to temporarily
/// override <c>MaximumIterationsPerRequest</c> for the current request.
/// </summary>
public static class MaxIterationsOverrideContext
{
    private static readonly AsyncLocal<int?> Current = new();

    /// <summary>Gets the current override, or null if not set.</summary>
    public static int? Value => Current.Value;

    /// <summary>
    /// Sets the max iterations override for the current async flow. Returns a
    /// disposable that restores the previous value on dispose.
    /// </summary>
    public static IDisposable Set(int? maxIterations)
    {
        var previous = Current.Value;
        Current.Value = maxIterations;
        return new Scope(previous);
    }

    private sealed class Scope(int? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

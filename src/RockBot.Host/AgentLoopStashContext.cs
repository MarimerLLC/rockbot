using System.Collections.Concurrent;

namespace RockBot.Host;

/// <summary>
/// Ambient per-async-flow handle to the per-run <see cref="ToolResultStashRegistry"/>
/// and the per-callId args summary dictionary. Set by <see cref="AgentLoopRunner.RunAsync"/>;
/// read by <see cref="RockBotFunctionInvokingChatClient"/> so the native tool path can
/// record args summaries and the trim algorithm can consult the registry from inside a
/// singleton without plumbing both objects through the M.E.AI middleware chain.
/// </summary>
internal static class AgentLoopStashContext
{
    /// <summary>
    /// Per-async-flow state for the stash feature.
    /// </summary>
    public sealed class State
    {
        public ToolResultStashRegistry Registry { get; } = new();
        public ConcurrentDictionary<string, string> ArgsSummaries { get; } =
            new(StringComparer.Ordinal);
        public string? SessionId { get; init; }
    }

    private static readonly AsyncLocal<State?> Current = new();

    /// <summary>Gets the active stash state, or null when no caller has set it.</summary>
    public static State? Value => Current.Value;

    /// <summary>
    /// Binds <paramref name="state"/> to the current async flow. Returns a disposable
    /// that restores the previous value on dispose.
    /// </summary>
    public static IDisposable Set(State? state)
    {
        var previous = Current.Value;
        Current.Value = state;
        return new Scope(previous);
    }

    private sealed class Scope(State? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

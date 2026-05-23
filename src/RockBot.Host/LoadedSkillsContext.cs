namespace RockBot.Host;

/// <summary>
/// Ambient per-async-flow state for skill-body aging. BM25 rank-1 skill recall in
/// <see cref="AgentContextBuilder"/> injects each top-hit skill as a system message
/// formatted <c>"Skill: {name}\n{content}"</c>. Those bodies stay in context for the
/// entire inner tool-call loop and accumulate — production logs show ~3,000 chars per
/// loaded skill remaining visible to the model long after the relevant lookup is done.
///
/// This context lets <see cref="RockBotFunctionInvokingChatClient"/> age each loaded
/// skill body across the per-tool-call boundary and unload it when the model hasn't
/// referenced it (via a follow-up <c>get_skill</c>) within
/// <see cref="AgentHostOptions.SkillBodyUnloadAfterIterations"/> iterations.
///
/// Unloading is non-destructive to subagent character — the model can re-fetch the
/// body at any time by calling <c>get_skill</c> again.
/// </summary>
internal static class LoadedSkillsContext
{
    /// <summary>Per-async-flow state.</summary>
    public sealed class State
    {
        /// <summary>
        /// For each skill body currently being tracked, the
        /// <see cref="CurrentIteration"/> value at which it was last referenced —
        /// either by the model calling <c>get_skill(name=...)</c> or, when no use is
        /// observed, the iteration at which the body was first discovered in context.
        /// </summary>
        public Dictionary<string, int> LastUseIteration { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Monotonic per-FICC-call counter. Bumped once per
        /// <see cref="RockBotFunctionInvokingChatClient.InvokeFunctionAsync"/> entry.
        /// </summary>
        public int CurrentIteration { get; set; }
    }

    private static readonly AsyncLocal<State?> Current = new();

    /// <summary>Gets the active state, or null when no caller has set it.</summary>
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

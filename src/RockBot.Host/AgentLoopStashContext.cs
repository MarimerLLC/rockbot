using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

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

        /// <summary>
        /// Watermark (tokens) for the pass now running, resolved from the run's tier by
        /// <see cref="AgentLoopRunner"/> so the native loop trims to the same budget. Null
        /// falls back to <see cref="AgentHostOptions.ToolResultStashWatermarkTokens"/>.
        /// </summary>
        public int? WatermarkTokens { get; set; }

        /// <summary>Whether this run has already warned that the trim can't reach its budget.</summary>
        public bool BudgetUnreachableWarned { get; set; }

        /// <summary>Registry entries already rendered into a stash-registry message.</summary>
        public int RenderedRegistryCount { get; set; }

        /// <summary>
        /// First message of the run's conversation. The native loop places context notes only
        /// into requests that start with it, so a side call (completion evaluator, decision
        /// model) made from the same async flow never picks one up.
        /// </summary>
        public ChatMessage? NoteRoot { get; set; }

        /// <summary>
        /// Context notes placed inside the native loop and the message each follows. The
        /// loop's history is a copy, so <see cref="AgentLoopRunner"/> splices these into its
        /// own list afterwards, in the same positions the provider saw them.
        /// </summary>
        public List<ContextNotes.Placed> PlacedNotes { get; } = [];
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

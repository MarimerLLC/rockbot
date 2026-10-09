using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Per-run mid-turn tier escalation (#663). A turn routed Low that issues a side-effecting
/// tool call (<see cref="ToolSideEffects"/>) or accumulates
/// <see cref="ToolErrorThreshold"/> tool errors moves to Balanced for the rest of its loop:
/// the small model picked the turn, but a capable one should finish it.
/// <para>
/// <see cref="AgentLoopRunner.RunAsync"/> binds a <see cref="State"/> for runs that opt in
/// (Low tier, <c>allowTierEscalation</c>). Tool dispatch on both loop paths reports calls and
/// results to it. The text-based loop and the outer native calls ask
/// <see cref="EffectiveTier"/> which tier to request; inside a native
/// <see cref="RockBotFunctionInvokingChatClient"/> loop that already started on Low,
/// <see cref="TierEscalatingChatClient"/> redirects the remaining iterations to the Balanced
/// client. Every nested run binds its own state (or none), so a subagent started from inside
/// a tool call never inherits its parent's escalation.
/// </para>
/// </summary>
public static class TierEscalationContext
{
    /// <summary>Tool errors in one run that trigger escalation.</summary>
    public const int ToolErrorThreshold = 2;

    private static readonly AsyncLocal<State?> Current = new();

    /// <summary>The escalation state bound to this async flow, or null when the run does not escalate.</summary>
    public static State? Value => Current.Value;

    /// <summary>Binds <paramref name="state"/> (possibly null) to the current async flow.</summary>
    public static IDisposable Set(State? state)
    {
        var previous = Current.Value;
        Current.Value = state;
        return new Scope(previous);
    }

    /// <summary>
    /// The tier a main-loop LLM call should request: <paramref name="requested"/>, raised to the
    /// escalated tier once the bound run has escalated. Auxiliary calls (completion evaluator,
    /// follow-up assessment) do not go through this and keep their own tier.
    /// </summary>
    public static ModelTier EffectiveTier(ModelTier requested) =>
        Current.Value is { EscalatedTo: { } escalated } && escalated > requested
            ? escalated
            : requested;

    /// <summary>
    /// True when a client below the function-invoking loop should send this request to the
    /// escalation target: the run has escalated and the request belongs to its main loop.
    /// </summary>
    public static bool ShouldRedirect =>
        Current.Value is { EscalatedTo: not null, InPrimaryCall: true };

    /// <summary>
    /// Marks the main-loop LLM call in progress, so <see cref="TierEscalatingChatClient"/>
    /// redirects only that call's iterations and not auxiliary Low-tier calls the run makes.
    /// </summary>
    public static IDisposable EnterPrimaryCall()
    {
        if (Current.Value is not { } state)
            return NullScope.Instance;
        var previous = state.InPrimaryCall;
        state.InPrimaryCall = true;
        return new PrimaryCallScope(state, previous);
    }

    /// <summary>
    /// Mutable escalation state for one <see cref="AgentLoopRunner.RunAsync"/> call. Shared by
    /// reference across the run's async flow so a tool call observed deep inside the
    /// function-invoking loop is visible to the next LLM request.
    /// </summary>
    public sealed class State(ModelTier runTier, ILogger? logger = null, LoopDiagnostics? diagnostics = null)
    {
        private readonly Lock _lock = new();
        private int _toolErrors;

        /// <summary>The tier the run was routed to.</summary>
        public ModelTier RunTier { get; } = runTier;

        /// <summary>The tier the run escalated to, or null while it has not.</summary>
        public ModelTier? EscalatedTo { get; private set; }

        /// <summary>Why the run escalated, or null while it has not.</summary>
        public string? Reason { get; private set; }

        /// <summary>True while the run's main-loop LLM call is in progress.</summary>
        public bool InPrimaryCall { get; internal set; }

        /// <summary>Reports a tool call about to run. A side-effecting call escalates the run.</summary>
        public void ObserveToolCall(string toolName, IEnumerable<KeyValuePair<string, object?>>? arguments)
        {
            if (EscalatedTo is not null)
                return;
            if (ToolSideEffects.IsSideEffecting(toolName, arguments, out var effectiveName))
                Escalate($"side-effecting tool call {effectiveName}");
        }

        /// <summary>Reports a finished tool call. The <see cref="ToolErrorThreshold"/>-th error escalates the run.</summary>
        public void ObserveToolResult(string toolName, bool isError)
        {
            if (!isError || EscalatedTo is not null)
                return;
            var errors = Interlocked.Increment(ref _toolErrors);
            if (errors >= ToolErrorThreshold)
                Escalate($"{errors} tool errors (last: {toolName})");
        }

        private void Escalate(string reason)
        {
            if (RunTier >= ModelTier.Balanced)
                return;

            lock (_lock)
            {
                if (EscalatedTo is not null)
                    return;
                EscalatedTo = ModelTier.Balanced;
                Reason = reason;
            }

            logger?.LogInformation("Tier escalated {From}→{To} mid-turn: {Reason}",
                RunTier, ModelTier.Balanced, reason);

            if (diagnostics is not null)
            {
                diagnostics.EscalatedTier = ModelTier.Balanced;
                diagnostics.EscalationReason = reason;
            }
        }
    }

    private sealed class Scope(State? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    private sealed class PrimaryCallScope(State state, bool previous) : IDisposable
    {
        public void Dispose() => state.InPrimaryCall = previous;
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Sits under the Low tier's <see cref="RockBotFunctionInvokingChatClient"/> and sends a
/// request to <paramref name="escalationTarget"/> (the Balanced tier's client) once the run
/// has escalated mid-turn (<see cref="TierEscalationContext.ShouldRedirect"/>). The
/// function-invoking loop owns the iterations of a native turn, so this is the one point
/// where the iterations after a side-effecting call can change model. Otherwise a pass-through.
/// </summary>
public sealed class TierEscalatingChatClient(IChatClient innerClient, IChatClient escalationTarget)
    : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        TierEscalationContext.ShouldRedirect
            ? escalationTarget.GetResponseAsync(messages, options, cancellationToken)
            : base.GetResponseAsync(messages, options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        TierEscalationContext.ShouldRedirect
            ? escalationTarget.GetStreamingResponseAsync(messages, options, cancellationToken)
            : base.GetStreamingResponseAsync(messages, options, cancellationToken);
}

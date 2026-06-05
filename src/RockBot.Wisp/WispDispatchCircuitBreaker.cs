namespace RockBot.Wisp;

/// <summary>
/// Process-wide guard against a runaway re-dispatch of the <em>same</em> wisp. Every
/// wisp flows through <see cref="SpawnWispsExecutor"/>, which is the only choke point
/// that survives across agent-loop invocations, completion-eval reprompts, scheduled
/// re-fires, and message/A2A re-triggers — none of which the per-loop
/// <c>RepetitiveToolCallDetector</c> can see, since it is rebuilt fresh per
/// <c>AgentLoopRunner.RunAsync</c> and dies with that loop.
///
/// <para>The breaker keeps a fixed-window dispatch count keyed by the exact definition
/// hash. When the same definition is dispatched more than
/// <see cref="WispOptions.DispatchCircuitBreakerMaxPerWindow"/> times within
/// <see cref="WispOptions.DispatchCircuitBreakerWindow"/>, further dispatches of that
/// definition are refused until the window rolls over. Keying on the exact definition
/// hash (not the value-stripped shape hash) means legitimately-varying wisps — same
/// shape, different dates/ids — never trip it; only a truly identical dispatch loop
/// does. A fixed window keeps memory at O(distinct definitions) regardless of dispatch
/// rate, which matters precisely in the runaway case the breaker exists to contain.</para>
/// </summary>
public sealed class WispDispatchCircuitBreaker
{
    private readonly WispOptions _options;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    /// <summary>Idle-definition sweep runs when the map grows past this many entries.</summary>
    private const int SweepThreshold = 1_024;

    private struct Window
    {
        public DateTimeOffset Start;
        public int Count;
    }

    /// <summary>The outcome of an <see cref="Admit"/> check.</summary>
    /// <param name="Allowed">False when the breaker is tripped and the dispatch must be refused.</param>
    /// <param name="Count">Dispatch count for this definition within the current window (including this attempt).</param>
    /// <param name="Window">The rolling window the count is measured over.</param>
    public readonly record struct Decision(bool Allowed, int Count, TimeSpan Window);

    public WispDispatchCircuitBreaker(WispOptions options, TimeProvider? timeProvider = null)
    {
        _options = options;
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Records a dispatch attempt for <paramref name="definitionHash"/> and reports
    /// whether it is permitted. Every attempt is counted (so a sustained runaway stays
    /// tripped for the rest of the window); only the count crossing the threshold flips
    /// <see cref="Decision.Allowed"/> to false.
    /// </summary>
    public Decision Admit(string definitionHash)
    {
        var window = _options.DispatchCircuitBreakerWindow;
        var max = _options.DispatchCircuitBreakerMaxPerWindow;

        if (!_options.DispatchCircuitBreakerEnabled || max <= 0 || window <= TimeSpan.Zero
            || string.IsNullOrEmpty(definitionHash))
            return new Decision(Allowed: true, Count: 0, Window: window);

        var now = _clock.GetUtcNow();

        lock (_gate)
        {
            if (!_windows.TryGetValue(definitionHash, out var w) || now - w.Start >= window)
                w = new Window { Start = now, Count = 0 };

            // A fixed window resets every `window`, so Count stays bounded by
            // (dispatch rate × window) and cannot realistically overflow.
            w.Count++;
            _windows[definitionHash] = w;

            if (_windows.Count > SweepThreshold)
                SweepExpired(now, window);

            return new Decision(Allowed: w.Count <= max, Count: w.Count, Window: window);
        }
    }

    /// <summary>Drops definitions whose window has fully expired. Caller holds <see cref="_gate"/>.</summary>
    private void SweepExpired(DateTimeOffset now, TimeSpan window)
    {
        var stale = new List<string>();
        foreach (var (hash, w) in _windows)
            if (now - w.Start >= window)
                stale.Add(hash);
        foreach (var hash in stale)
            _windows.Remove(hash);
    }
}

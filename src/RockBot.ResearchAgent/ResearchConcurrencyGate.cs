namespace RockBot.ResearchAgent;

/// <summary>Settings for the research server (configuration section <c>Research</c>).</summary>
public sealed class ResearchOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Research";

    /// <summary>
    /// How many research runs one process works on at once; more wait their turn. Bounds LLM and
    /// search spend under a burst. Defaults to 6: an advisor council fans out pre-research plus a
    /// call per persona (five or so) at once, and at 3 half of them queued past their timeout.
    /// </summary>
    public int MaxConcurrentRuns { get; set; } = 6;

    /// <summary>
    /// How long a clarifying question's <c>requestState</c> stays valid (MRTR callers). An answer
    /// that comes back later re-scopes instead.
    /// </summary>
    public TimeSpan ScopeTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Caps concurrent research runs per process at <see cref="ResearchOptions.MaxConcurrentRuns"/>,
/// however the calls arrive (plain calls and tasks alike).
/// </summary>
internal sealed class ResearchConcurrencyGate(int maxConcurrentRuns) : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(Math.Max(1, maxConcurrentRuns), Math.Max(1, maxConcurrentRuns));
    private int _running;

    /// <summary>The cap.</summary>
    public int MaxConcurrentRuns { get; } = Math.Max(1, maxConcurrentRuns);

    /// <summary>Runs in progress right now.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Waits for a free slot; dispose the result to release it.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _running);
        return new Slot(this);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Slot(ResearchConcurrencyGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            Interlocked.Decrement(ref gate._running);
            gate._semaphore.Release();
        }
    }
}

/// <summary>
/// The <see cref="IResearchRunner"/> callers get: waits for a slot in the
/// <see cref="ResearchConcurrencyGate"/>, then runs <paramref name="inner"/>.
/// </summary>
internal sealed class GatedResearchRunner(IResearchRunner inner, ResearchConcurrencyGate gate) : IResearchRunner
{
    public async Task<ResearchOutcome> RunAsync(
        ResearchRequest request,
        string runId,
        Func<string, CancellationToken, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        using var slot = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        return await inner.RunAsync(request, runId, onProgress, cancellationToken).ConfigureAwait(false);
    }
}

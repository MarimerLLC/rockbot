using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Caps concurrent deliberations per process at <see cref="CouncilOptions.MaxConcurrentRuns"/>,
/// however the calls arrive (plain calls and tasks alike). Same shape as the research server's
/// <c>ResearchConcurrencyGate</c>.
/// </summary>
internal sealed class CouncilConcurrencyGate(int maxConcurrentRuns) : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(Math.Max(1, maxConcurrentRuns), Math.Max(1, maxConcurrentRuns));
    private int _running;

    /// <summary>The cap.</summary>
    public int MaxConcurrentRuns { get; } = Math.Max(1, maxConcurrentRuns);

    /// <summary>Deliberations in progress right now.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Waits for a free slot; dispose the result to release it.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _running);
        return new Slot(this);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Slot(CouncilConcurrencyGate gate) : IDisposable
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
/// The <see cref="ICouncil"/> the tool gets: Select runs freely (one short Balanced call), the
/// deliberation waits for a slot in the <see cref="CouncilConcurrencyGate"/>.
/// </summary>
internal sealed class GatedCouncil(ICouncil inner, CouncilConcurrencyGate gate) : ICouncil
{
    public Task<SelectorOutput> SelectAsync(CouncilRequest request, CancellationToken ct) =>
        inner.SelectAsync(request, ct);

    public async Task<CouncilResponse> DeliberateAsync(
        CouncilRequest request, SelectorOutput selection, string runId, CancellationToken ct)
    {
        using var slot = await gate.EnterAsync(ct).ConfigureAwait(false);
        return await inner.DeliberateAsync(request, selection, runId, ct).ConfigureAwait(false);
    }
}

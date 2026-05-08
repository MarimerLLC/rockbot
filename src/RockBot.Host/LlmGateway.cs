using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Global per-tier concurrency layer for all LLM calls. Every call to
/// <see cref="ILlmClient"/> flows through here so that bursty parallel work
/// (e.g. observation-framework extraction) cannot overwhelm a tier.
/// </summary>
/// <remarks>
/// <para>
/// Cancellation is the priority mechanism. When a caller's <c>ct</c> fires, the
/// pending wait on the per-tier <see cref="SemaphoreSlim"/> aborts immediately,
/// freeing the slot for other waiters. This is how user-initiated work effectively
/// preempts dream-cycle work without an explicit priority queue: the work-serializer
/// already cancels the dream when a user message arrives, and that cancellation
/// drains the dream's queued LLM calls.
/// </para>
/// <para>
/// Registered as a singleton so all callers share the same per-tier semaphores.
/// </para>
/// <para>
/// See <c>design/llm-gateway.md</c> for the full design.
/// </para>
/// </remarks>
internal sealed class LlmGateway : ILlmGateway, IDisposable
{
    private readonly TierSlot[] _slots;
    private readonly ILogger<LlmGateway> _logger;

    public LlmGateway(IOptions<LlmGatewayOptions> options, ILogger<LlmGateway> logger)
    {
        var opts = options.Value;

        var tierValues = (ModelTier[])Enum.GetValues(typeof(ModelTier));
        _slots = new TierSlot[tierValues.Length];
        foreach (var tier in tierValues)
        {
            var (concurrent, pending) = tier switch
            {
                ModelTier.Low => (opts.LowMaxConcurrent, opts.LowMaxPending),
                ModelTier.High => (opts.HighMaxConcurrent, opts.HighMaxPending),
                _ => (opts.BalancedMaxConcurrent, opts.BalancedMaxPending),
            };

            if (concurrent < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"LlmGatewayOptions {tier}MaxConcurrent must be >= 1 (was {concurrent}).");

            if (pending < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"LlmGatewayOptions {tier}MaxPending must be >= 0 (was {pending}).");

            _slots[(int)tier] = new TierSlot(concurrent, pending);
        }

        _logger = logger;

        _logger.LogInformation(
            "LlmGateway: per-tier caps " +
            "Low={LowConcurrent}+{LowPending} " +
            "Balanced={BalancedConcurrent}+{BalancedPending} " +
            "High={HighConcurrent}+{HighPending} " +
            "(MaxConcurrent + MaxPending)",
            opts.LowMaxConcurrent, opts.LowMaxPending,
            opts.BalancedMaxConcurrent, opts.BalancedMaxPending,
            opts.HighMaxConcurrent, opts.HighMaxPending);
    }

    /// <summary>
    /// Returns the current number of waiters on the per-tier semaphore. Useful for
    /// diagnostics and tests; values are observational and may race.
    /// </summary>
    internal int GetPendingCount(ModelTier tier) => Volatile.Read(ref _slots[(int)tier].Pending);

    /// <summary>
    /// Returns the current number of in-flight calls on the tier. Useful for
    /// diagnostics and tests; values are observational and may race.
    /// </summary>
    internal int GetInFlightCount(ModelTier tier) => Volatile.Read(ref _slots[(int)tier].InFlight);

    public async Task<T> ExecuteAsync<T>(
        ModelTier tier,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var slot = _slots[(int)tier];
        var tierTag = new KeyValuePair<string, object?>("rockbot.llm.tier", tier.ToString());

        // Bounded queue: atomically reserve a "ticket" (counted against in-flight + queued).
        // If the tier is at its cap, fail fast rather than queuing indefinitely.
        var capacityCap = slot.MaxConcurrent + slot.MaxPending;
        var active = Interlocked.Increment(ref slot.Active);
        if (active > capacityCap)
        {
            Interlocked.Decrement(ref slot.Active);
            HostDiagnostics.LlmGatewaySaturationRejections.Add(1, tierTag);
            _logger.LogWarning(
                "LlmGateway: tier {Tier} saturated (active={Active} > cap={Cap}); rejecting call",
                tier, active, capacityCap);
            throw new LlmGatewaySaturatedException(tier, capacityCap);
        }

        try
        {
            var slotWaitSw = Stopwatch.StartNew();
            Interlocked.Increment(ref slot.Pending);
            try
            {
                await slot.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref slot.Pending);
                slotWaitSw.Stop();
                HostDiagnostics.LlmGatewaySlotWaitDuration.Record(
                    slotWaitSw.Elapsed.TotalMilliseconds, tierTag);
            }

            Interlocked.Increment(ref slot.InFlight);
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref slot.InFlight);
                slot.Semaphore.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref slot.Active);
        }
    }

    public void Dispose()
    {
        foreach (var slot in _slots)
            slot.Semaphore.Dispose();
    }

    private sealed class TierSlot
    {
        public readonly SemaphoreSlim Semaphore;
        public readonly int MaxConcurrent;
        public readonly int MaxPending;

        /// <summary>Callers waiting on the semaphore (have not yet acquired a slot).</summary>
        public int Pending;

        /// <summary>Callers currently running their operation.</summary>
        public int InFlight;

        /// <summary>Total callers active on this tier (Pending + InFlight). Used for the bounded-queue cap.</summary>
        public int Active;

        public TierSlot(int maxConcurrent, int maxPending)
        {
            Semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            MaxConcurrent = maxConcurrent;
            MaxPending = maxPending;
        }
    }
}

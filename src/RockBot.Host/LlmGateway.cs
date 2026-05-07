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
    private readonly ILlmRateLimitClassifier _classifier;
    private readonly ILogger<LlmGateway> _logger;
    private readonly int _maxRetries;
    private readonly int _maxBackoffSeconds;

    public LlmGateway(
        IOptions<LlmGatewayOptions> options,
        ILlmRateLimitClassifier classifier,
        ILogger<LlmGateway> logger)
    {
        var opts = options.Value;

        var tierValues = (ModelTier[])Enum.GetValues(typeof(ModelTier));
        _slots = new TierSlot[tierValues.Length];
        foreach (var tier in tierValues)
        {
            var cap = tier switch
            {
                ModelTier.Low => opts.LowMaxConcurrent,
                ModelTier.High => opts.HighMaxConcurrent,
                _ => opts.BalancedMaxConcurrent,
            };

            if (cap < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"LlmGatewayOptions {tier}MaxConcurrent must be >= 1 (was {cap}).");

            _slots[(int)tier] = new TierSlot(cap);
        }

        if (opts.MaxRateLimitRetries < 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"LlmGatewayOptions.MaxRateLimitRetries must be >= 0 (was {opts.MaxRateLimitRetries}).");

        if (opts.MaxBackoffSeconds < 1)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"LlmGatewayOptions.MaxBackoffSeconds must be >= 1 (was {opts.MaxBackoffSeconds}).");

        _classifier = classifier;
        _logger = logger;
        _maxRetries = opts.MaxRateLimitRetries;
        _maxBackoffSeconds = opts.MaxBackoffSeconds;

        _logger.LogInformation(
            "LlmGateway: per-tier concurrency caps Low={Low} Balanced={Balanced} High={High}, " +
            "rate-limit retries Max={MaxRetries} backoff cap={MaxBackoff}s",
            opts.LowMaxConcurrent, opts.BalancedMaxConcurrent, opts.HighMaxConcurrent,
            _maxRetries, _maxBackoffSeconds);
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
            return await ExecuteWithRetryAsync(tier, tierTag, operation, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref slot.InFlight);
            slot.Semaphore.Release();
        }
    }

    /// <summary>
    /// Invokes <paramref name="operation"/> and, on rate-limit (HTTP 429) failures,
    /// retries up to <c>MaxRateLimitRetries</c> times. The slot is held throughout
    /// — releasing during retry waits does not help, since rate limits are per-tier
    /// so any other call in the same tier would hit the same limit.
    /// </summary>
    private async Task<T> ExecuteWithRetryAsync<T>(
        ModelTier tier,
        KeyValuePair<string, object?> tierTag,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                attempt < _maxRetries
                && !cancellationToken.IsCancellationRequested
                && _classifier.TryClassify(ex, out var classifierRetryAfter))
            {
                attempt++;

                var source = classifierRetryAfter.HasValue ? "header" : "backoff";
                var wait = classifierRetryAfter ?? ComputeBackoff(attempt);

                _logger.LogWarning(
                    "LlmGateway: rate-limit on tier {Tier} (attempt {Attempt}/{Max}); " +
                    "waiting {WaitSeconds}s ({Source}) before retry",
                    tier, attempt, _maxRetries, wait.TotalSeconds, source);

                HostDiagnostics.LlmGatewayRateLimitRetries.Add(
                    1,
                    tierTag,
                    new KeyValuePair<string, object?>("rockbot.llm.gateway.retry_after_source", source));

                try
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        // 1s, 2s, 4s, 8s, ..., capped at MaxBackoffSeconds.
        // attempt is 1-based.
        var seconds = Math.Min(Math.Pow(2, attempt - 1), _maxBackoffSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    public void Dispose()
    {
        foreach (var slot in _slots)
            slot.Semaphore.Dispose();
    }

    private sealed class TierSlot
    {
        public readonly SemaphoreSlim Semaphore;
        public int Pending;
        public int InFlight;

        public TierSlot(int maxConcurrent)
        {
            Semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        }
    }
}

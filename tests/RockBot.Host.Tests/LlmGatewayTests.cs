using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class LlmGatewayTests
{
    private static LlmGateway CreateGateway(
        int low = 2,
        int balanced = 2,
        int high = 2,
        int maxRetries = 0,
        int maxBackoffSeconds = 16,
        ILlmRateLimitClassifier? classifier = null)
    {
        var options = Options.Create(new LlmGatewayOptions
        {
            LowMaxConcurrent = low,
            BalancedMaxConcurrent = balanced,
            HighMaxConcurrent = high,
            MaxRateLimitRetries = maxRetries,
            MaxBackoffSeconds = maxBackoffSeconds,
        });
        return new LlmGateway(
            options,
            classifier ?? new NeverRateLimitClassifier(),
            NullLogger<LlmGateway>.Instance);
    }

    /// <summary>Stub classifier that never reports rate-limit conditions.</summary>
    private sealed class NeverRateLimitClassifier : ILlmRateLimitClassifier
    {
        public bool TryClassify(Exception exception, out TimeSpan? retryAfter)
        {
            retryAfter = null;
            return false;
        }
    }

    /// <summary>
    /// Stub classifier that recognises a custom marker exception as rate-limit and
    /// surfaces an optional Retry-After hint carried on the exception.
    /// </summary>
    private sealed class FakeRateLimitException(TimeSpan? retryAfter = null) : Exception("simulated 429")
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    private sealed class FakeRateLimitClassifier : ILlmRateLimitClassifier
    {
        public bool TryClassify(Exception exception, out TimeSpan? retryAfter)
        {
            // Walk the inner-exception chain so wrapped throws are still recognised.
            var current = exception;
            while (current is not null)
            {
                if (current is FakeRateLimitException frle)
                {
                    retryAfter = frle.RetryAfter;
                    return true;
                }
                current = current.InnerException;
            }
            retryAfter = null;
            return false;
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_RunsOperationAndReturnsResult()
    {
        using var gateway = CreateGateway();

        var result = await gateway.ExecuteAsync(
            ModelTier.Balanced,
            ct => Task.FromResult(42),
            CancellationToken.None);

        Assert.AreEqual(42, result);
    }

    [TestMethod]
    public async Task ExecuteAsync_PropagatesCancellationToOperation()
    {
        using var gateway = CreateGateway();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // SemaphoreSlim.WaitAsync may throw the derived TaskCanceledException
        // when the token is cancelled — accept any OperationCanceledException.
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await gateway.ExecuteAsync<int>(
                ModelTier.Balanced,
                ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(0);
                },
                cts.Token));
    }

    [TestMethod]
    public async Task ExecuteAsync_EnforcesPerTierConcurrencyCap()
    {
        using var gateway = CreateGateway(low: 2);
        var inFlight = 0;
        var maxInFlight = 0;
        var gate = new TaskCompletionSource();

        async Task<int> Op(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref inFlight);
            // Race-tolerant max tracking
            int observed;
            do
            {
                observed = Volatile.Read(ref maxInFlight);
                if (current <= observed) break;
            } while (Interlocked.CompareExchange(ref maxInFlight, current, observed) != observed);

            await gate.Task;
            Interlocked.Decrement(ref inFlight);
            return 1;
        }

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => gateway.ExecuteAsync(ModelTier.Low, Op, CancellationToken.None))
            .ToArray();

        // Wait long enough for the first batch to actually be running
        await WaitUntilAsync(() => Volatile.Read(ref inFlight) == 2, TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, Volatile.Read(ref inFlight),
            "Cap should serialize callers at MaxConcurrent");

        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.AreEqual(2, Volatile.Read(ref maxInFlight),
            "MaxConcurrent should never have been exceeded");
    }

    [TestMethod]
    public async Task ExecuteAsync_TiersDoNotShareSlots()
    {
        using var gateway = CreateGateway(low: 1, balanced: 1, high: 1);
        var lowGate = new TaskCompletionSource();
        var balancedGate = new TaskCompletionSource();
        var highGate = new TaskCompletionSource();

        var lowTask = gateway.ExecuteAsync(ModelTier.Low, async ct =>
        {
            await lowGate.Task;
            return 0;
        }, CancellationToken.None);

        var balancedTask = gateway.ExecuteAsync(ModelTier.Balanced, async ct =>
        {
            await balancedGate.Task;
            return 0;
        }, CancellationToken.None);

        var highTask = gateway.ExecuteAsync(ModelTier.High, async ct =>
        {
            await highGate.Task;
            return 0;
        }, CancellationToken.None);

        // All three should be in-flight despite each tier's cap of 1
        await WaitUntilAsync(
            () => gateway.GetInFlightCount(ModelTier.Low) == 1
               && gateway.GetInFlightCount(ModelTier.Balanced) == 1
               && gateway.GetInFlightCount(ModelTier.High) == 1,
            TimeSpan.FromSeconds(5));

        lowGate.SetResult();
        balancedGate.SetResult();
        highGate.SetResult();
        await Task.WhenAll(lowTask, balancedTask, highTask);
    }

    [TestMethod]
    public async Task ExecuteAsync_CancellationWhileWaitingForSlot_AbortsAndDoesNotConsumeSlot()
    {
        using var gateway = CreateGateway(low: 1);
        var gate = new TaskCompletionSource();

        // Occupy the only slot
        var holder = gateway.ExecuteAsync(ModelTier.Low, async ct =>
        {
            await gate.Task;
            return 0;
        }, CancellationToken.None);

        await WaitUntilAsync(
            () => gateway.GetInFlightCount(ModelTier.Low) == 1,
            TimeSpan.FromSeconds(5));

        // Try to enqueue a second call with a CT that cancels before the slot frees
        using var cts = new CancellationTokenSource();
        var operationRan = false;
        var blocked = gateway.ExecuteAsync<int>(ModelTier.Low, ct =>
        {
            operationRan = true;
            return Task.FromResult(0);
        }, cts.Token);

        await WaitUntilAsync(
            () => gateway.GetPendingCount(ModelTier.Low) == 1,
            TimeSpan.FromSeconds(5));

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await blocked);

        Assert.IsFalse(operationRan, "Cancelled waiter must not run the operation");

        // The pending count should drop to zero; the holder still owns the in-flight slot
        await WaitUntilAsync(
            () => gateway.GetPendingCount(ModelTier.Low) == 0,
            TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, gateway.GetInFlightCount(ModelTier.Low));

        // Free the holder; subsequent call should proceed normally (slot wasn't leaked)
        gate.SetResult();
        await holder;

        var followup = await gateway.ExecuteAsync(ModelTier.Low,
            ct => Task.FromResult(99),
            CancellationToken.None);
        Assert.AreEqual(99, followup);
    }

    [TestMethod]
    public async Task ExecuteAsync_ExceptionInOperation_ReleasesSlot()
    {
        using var gateway = CreateGateway(low: 1);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await gateway.ExecuteAsync<int>(
                ModelTier.Low,
                ct => throw new InvalidOperationException("boom"),
                CancellationToken.None));

        // Slot should be free for the next call
        var ok = await gateway.ExecuteAsync(
            ModelTier.Low,
            ct => Task.FromResult(7),
            CancellationToken.None);
        Assert.AreEqual(7, ok);
        Assert.AreEqual(0, gateway.GetInFlightCount(ModelTier.Low));
    }

    [TestMethod]
    public async Task ExecuteAsync_NullOperation_Throws()
    {
        using var gateway = CreateGateway();

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await gateway.ExecuteAsync<int>(
                ModelTier.Balanced,
                operation: null!,
                CancellationToken.None));
    }

    [TestMethod]
    public void Constructor_CapBelowOne_Throws()
    {
        var bad = Options.Create(new LlmGatewayOptions { LowMaxConcurrent = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new LlmGateway(bad, new NeverRateLimitClassifier(), NullLogger<LlmGateway>.Instance));
    }

    [TestMethod]
    public void Constructor_NegativeMaxRetries_Throws()
    {
        var bad = Options.Create(new LlmGatewayOptions { MaxRateLimitRetries = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new LlmGateway(bad, new NeverRateLimitClassifier(), NullLogger<LlmGateway>.Instance));
    }

    [TestMethod]
    public void Constructor_MaxBackoffBelowOne_Throws()
    {
        var bad = Options.Create(new LlmGatewayOptions { MaxBackoffSeconds = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new LlmGateway(bad, new NeverRateLimitClassifier(), NullLogger<LlmGateway>.Instance));
    }

    [TestMethod]
    public async Task ExecuteAsync_NonRateLimitError_NotRetried()
    {
        using var gateway = CreateGateway(maxRetries: 5, classifier: new FakeRateLimitClassifier());
        var attempts = 0;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await gateway.ExecuteAsync<int>(
                ModelTier.Balanced,
                ct =>
                {
                    attempts++;
                    throw new InvalidOperationException("not a 429");
                },
                CancellationToken.None));

        Assert.AreEqual(1, attempts, "Non-rate-limit errors must not be retried");
    }

    [TestMethod]
    public async Task ExecuteAsync_RateLimit_RetriesAndSucceeds()
    {
        using var gateway = CreateGateway(
            maxRetries: 3,
            classifier: new FakeRateLimitClassifier());
        var attempts = 0;

        var result = await gateway.ExecuteAsync(
            ModelTier.Balanced,
            ct =>
            {
                attempts++;
                if (attempts < 3)
                    throw new FakeRateLimitException(retryAfter: TimeSpan.FromMilliseconds(1));
                return Task.FromResult(42);
            },
            CancellationToken.None);

        Assert.AreEqual(42, result);
        Assert.AreEqual(3, attempts, "Should have retried twice before success");
    }

    [TestMethod]
    public async Task ExecuteAsync_RateLimit_ExhaustsRetriesAndThrows()
    {
        using var gateway = CreateGateway(
            maxRetries: 2,
            classifier: new FakeRateLimitClassifier());
        var attempts = 0;

        await Assert.ThrowsExactlyAsync<FakeRateLimitException>(async () =>
            await gateway.ExecuteAsync<int>(
                ModelTier.Balanced,
                ct =>
                {
                    attempts++;
                    throw new FakeRateLimitException(retryAfter: TimeSpan.FromMilliseconds(1));
                },
                CancellationToken.None));

        // Initial attempt + 2 retries = 3 attempts total.
        Assert.AreEqual(3, attempts);
    }

    [TestMethod]
    public async Task ExecuteAsync_RateLimit_HonorsRetryAfter()
    {
        using var gateway = CreateGateway(
            maxRetries: 1,
            classifier: new FakeRateLimitClassifier());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var attempts = 0;

        await gateway.ExecuteAsync(
            ModelTier.Balanced,
            ct =>
            {
                attempts++;
                if (attempts == 1)
                    throw new FakeRateLimitException(retryAfter: TimeSpan.FromMilliseconds(200));
                return Task.FromResult(0);
            },
            CancellationToken.None);

        sw.Stop();
        Assert.AreEqual(2, attempts);
        Assert.IsTrue(sw.ElapsedMilliseconds >= 180,
            $"Expected at least ~200ms wait honoring Retry-After, but only {sw.ElapsedMilliseconds}ms elapsed");
    }

    [TestMethod]
    public async Task ExecuteAsync_RateLimit_CancelDuringWait_Aborts()
    {
        using var gateway = CreateGateway(
            maxRetries: 5,
            classifier: new FakeRateLimitClassifier());
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        var task = gateway.ExecuteAsync<int>(
            ModelTier.Balanced,
            ct =>
            {
                attempts++;
                throw new FakeRateLimitException(retryAfter: TimeSpan.FromSeconds(30));
            },
            cts.Token);

        // Let the first attempt run and start the retry wait.
        await WaitUntilAsync(() => attempts == 1, TimeSpan.FromSeconds(5));

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        Assert.AreEqual(1, attempts, "Cancellation should abort during the retry wait");
    }

    [TestMethod]
    public async Task ExecuteAsync_RateLimit_NoRetryAfter_UsesExponentialBackoff()
    {
        var maxBackoffSeconds = 1; // Cap backoff at 1s so the test runs quickly.
        using var gateway = CreateGateway(
            maxRetries: 1,
            maxBackoffSeconds: maxBackoffSeconds,
            classifier: new FakeRateLimitClassifier());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var attempts = 0;

        await gateway.ExecuteAsync(
            ModelTier.Balanced,
            ct =>
            {
                attempts++;
                if (attempts == 1)
                    throw new FakeRateLimitException(retryAfter: null);
                return Task.FromResult(0);
            },
            CancellationToken.None);

        sw.Stop();
        Assert.AreEqual(2, attempts);
        // Attempt 1 backoff is 2^0 = 1 second; capped at maxBackoff so still 1s.
        Assert.IsTrue(sw.ElapsedMilliseconds >= 900,
            $"Expected at least ~1s exponential backoff, but only {sw.ElapsedMilliseconds}ms elapsed");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Condition not met within {timeout}");
    }
}

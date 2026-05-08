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
        int lowMaxPending = 64,
        int balancedMaxPending = 64,
        int highMaxPending = 64)
    {
        var options = Options.Create(new LlmGatewayOptions
        {
            LowMaxConcurrent = low,
            BalancedMaxConcurrent = balanced,
            HighMaxConcurrent = high,
            LowMaxPending = lowMaxPending,
            BalancedMaxPending = balancedMaxPending,
            HighMaxPending = highMaxPending,
        });
        return new LlmGateway(options, NullLogger<LlmGateway>.Instance);
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
    public async Task ExecuteAsync_CancellationWhileInFlight_AbortsAndReleasesSlot()
    {
        using var gateway = CreateGateway(low: 1);
        using var cts = new CancellationTokenSource();
        var operationStarted = new TaskCompletionSource();

        // Operation respects ct: it parks until ct fires, then throws.
        var task = gateway.ExecuteAsync<int>(ModelTier.Low, async ct =>
        {
            operationStarted.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }, cts.Token);

        await operationStarted.Task;
        await WaitUntilAsync(
            () => gateway.GetInFlightCount(ModelTier.Low) == 1,
            TimeSpan.FromSeconds(5));

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);

        // Slot should have been released; in-flight back to zero
        await WaitUntilAsync(
            () => gateway.GetInFlightCount(ModelTier.Low) == 0,
            TimeSpan.FromSeconds(5));

        // And a follow-up call should proceed
        var followup = await gateway.ExecuteAsync(
            ModelTier.Low,
            ct => Task.FromResult(123),
            CancellationToken.None);
        Assert.AreEqual(123, followup);
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
            new LlmGateway(bad, NullLogger<LlmGateway>.Instance));
    }

    [TestMethod]
    public void Constructor_NegativeMaxPending_Throws()
    {
        var bad = Options.Create(new LlmGatewayOptions { LowMaxPending = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new LlmGateway(bad, NullLogger<LlmGateway>.Instance));
    }

    [TestMethod]
    public async Task ExecuteAsync_AtSaturation_ThrowsLlmGatewaySaturatedException()
    {
        // Cap = MaxConcurrent (1) + MaxPending (1) = 2 callers max.
        // Hold both with gates; the 3rd should fail fast.
        using var gateway = CreateGateway(low: 1, lowMaxPending: 1);
        var holdGate1 = new TaskCompletionSource();
        var holdGate2 = new TaskCompletionSource();

        // Caller 1: takes the slot
        var caller1 = gateway.ExecuteAsync(ModelTier.Low, async ct =>
        {
            await holdGate1.Task;
            return 0;
        }, CancellationToken.None);

        await WaitUntilAsync(
            () => gateway.GetInFlightCount(ModelTier.Low) == 1,
            TimeSpan.FromSeconds(5));

        // Caller 2: queued (slot occupied, but cap allows 1 pending)
        var caller2 = gateway.ExecuteAsync(ModelTier.Low, async ct =>
        {
            await holdGate2.Task;
            return 0;
        }, CancellationToken.None);

        await WaitUntilAsync(
            () => gateway.GetPendingCount(ModelTier.Low) == 1,
            TimeSpan.FromSeconds(5));

        // Caller 3: must fail fast
        var ex = await Assert.ThrowsExactlyAsync<LlmGatewaySaturatedException>(async () =>
            await gateway.ExecuteAsync(ModelTier.Low, ct => Task.FromResult(0), CancellationToken.None));

        Assert.AreEqual(ModelTier.Low, ex.Tier);
        Assert.AreEqual(2, ex.CapacityCap, "Cap should be MaxConcurrent + MaxPending");

        // Drain so callers 1 and 2 finish; this also confirms Active was decremented
        // correctly so the next call can proceed.
        holdGate1.SetResult();
        holdGate2.SetResult();
        await caller1;
        await caller2;

        // After drain, a new call should be admitted (Active counter unwound cleanly)
        var followup = await gateway.ExecuteAsync(
            ModelTier.Low,
            ct => Task.FromResult(99),
            CancellationToken.None);
        Assert.AreEqual(99, followup);
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectionDoesNotConsumeCapacity()
    {
        // Cap = 1 + 0 = 1. After a reject, we should still be able to make a call.
        using var gateway = CreateGateway(low: 1, lowMaxPending: 0);
        var gate = new TaskCompletionSource();

        var holder = gateway.ExecuteAsync(ModelTier.Low, async ct =>
        {
            await gate.Task;
            return 0;
        }, CancellationToken.None);

        await WaitUntilAsync(
            () => gateway.GetInFlightCount(ModelTier.Low) == 1,
            TimeSpan.FromSeconds(5));

        // Multiple rejections in a row — each must fully release the ticket.
        for (int i = 0; i < 5; i++)
        {
            await Assert.ThrowsExactlyAsync<LlmGatewaySaturatedException>(async () =>
                await gateway.ExecuteAsync(ModelTier.Low,
                    ct => Task.FromResult(0),
                    CancellationToken.None));
        }

        // Holder is still in-flight; Active should still be 1, not 6 from leaked tickets.
        Assert.AreEqual(1, gateway.GetInFlightCount(ModelTier.Low));

        gate.SetResult();
        await holder;

        // After holder finishes, Active is 0; new call accepted.
        var follow = await gateway.ExecuteAsync(
            ModelTier.Low,
            ct => Task.FromResult(7),
            CancellationToken.None);
        Assert.AreEqual(7, follow);
    }

    [TestMethod]
    public async Task ExecuteAsync_SaturationIsPerTier()
    {
        // Saturate Low; Balanced and High should still accept calls.
        using var gateway = CreateGateway(
            low: 1, balanced: 1, high: 1,
            lowMaxPending: 0, balancedMaxPending: 0, highMaxPending: 0);

        var lowGate = new TaskCompletionSource();
        var lowHolder = gateway.ExecuteAsync(ModelTier.Low, async ct =>
        {
            await lowGate.Task;
            return 0;
        }, CancellationToken.None);

        await WaitUntilAsync(
            () => gateway.GetInFlightCount(ModelTier.Low) == 1,
            TimeSpan.FromSeconds(5));

        // Low is saturated (cap=1, holder consumes it)
        await Assert.ThrowsExactlyAsync<LlmGatewaySaturatedException>(async () =>
            await gateway.ExecuteAsync(ModelTier.Low, ct => Task.FromResult(0), CancellationToken.None));

        // Balanced and High remain available
        var balancedResult = await gateway.ExecuteAsync(
            ModelTier.Balanced, ct => Task.FromResult(1), CancellationToken.None);
        var highResult = await gateway.ExecuteAsync(
            ModelTier.High, ct => Task.FromResult(2), CancellationToken.None);

        Assert.AreEqual(1, balancedResult);
        Assert.AreEqual(2, highResult);

        lowGate.SetResult();
        await lowHolder;
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

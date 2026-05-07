using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class LlmGatewayTests
{
    private static LlmGateway CreateGateway(int low = 2, int balanced = 2, int high = 2)
    {
        var options = Options.Create(new LlmGatewayOptions
        {
            LowMaxConcurrent = low,
            BalancedMaxConcurrent = balanced,
            HighMaxConcurrent = high,
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

using ModelContextProtocol.Server;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// End-to-end coverage of #604: calls in flight when the server reconnects or refreshes keep the
/// connection they started on, finish normally, and are never executed twice.
/// </summary>
[TestClass]
public class McpBridgeConcurrencyEndToEndTests
{
    private sealed class Counter
    {
        private int _value;
        public int Value => Volatile.Read(ref _value);
        public void Increment() => Interlocked.Increment(ref _value);
    }

    /// <summary>A tool that counts its executions and takes long enough to still be running when the test reconnects.</summary>
    private static McpServerTool SlowTool(Counter executions) => McpServerTool.Create(
        async (CancellationToken ct) =>
        {
            executions.Increment();
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            return "done";
        },
        new McpServerToolCreateOptions { Name = "slow_send" });

    private static McpServerTool OtherTool() => McpServerTool.Create(
        () => "other",
        new McpServerToolCreateOptions { Name = "other" });

    [TestMethod]
    public async Task CallsInFlight_SurviveAReconnect_AndRunOnce()
    {
        var executions = new Counter();
        await using var harness = await BridgeHarness.StartAsync([SlowTool(executions)]);

        var calls = Enumerable.Range(0, 8).Select(_ => harness.InvokeAsync("slow_send", "{}")).ToList();
        await WaitUntil(() => executions.Value == 8);

        // A reconnect replaces the client the calls hold with a new one.
        Assert.IsTrue(await harness.Bridge.ReconnectAsync(BridgeHarness.ServerName, CancellationToken.None));

        var results = await Task.WhenAll(calls);

        foreach (var result in results)
            Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual(8, executions.Value,
            "A call was cut off by the reconnect and executed again by the retry path.");
    }

    [TestMethod]
    public async Task CallsInFlight_SurviveASurfaceRefresh()
    {
        var executions = new Counter();
        await using var harness = await BridgeHarness.StartAsync([SlowTool(executions)]);

        var calls = Enumerable.Range(0, 4).Select(_ => harness.InvokeAsync("slow_send", "{}")).ToList();
        await WaitUntil(() => executions.Value == 4);

        harness.AddServerTool(OtherTool());
        Assert.IsTrue(await harness.Bridge.RefreshSurfaceAsync(BridgeHarness.ServerName, CancellationToken.None));

        var results = await Task.WhenAll(calls);
        foreach (var result in results)
            Assert.IsFalse(result.IsError, result.Content);
        Assert.AreEqual(4, executions.Value);

        var after = await harness.InvokeAsync("other", "{}");
        Assert.IsFalse(after.IsError, after.Content);
    }

    [TestMethod]
    public async Task ConcurrentReconnects_LeaveOneWorkingConnection()
    {
        await using var harness = await BridgeHarness.StartAsync([OtherTool()]);

        var reconnects = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => harness.Bridge.ReconnectAsync(BridgeHarness.ServerName, CancellationToken.None)));

        foreach (var reconnected in reconnects)
            Assert.IsTrue(reconnected);

        var result = await harness.InvokeAsync("other", "{}");
        Assert.IsFalse(result.IsError, result.Content);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for the calls to start.");
            await Task.Delay(10);
        }
    }
}

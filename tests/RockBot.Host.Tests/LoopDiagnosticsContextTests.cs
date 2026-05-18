namespace RockBot.Host.Tests;

[TestClass]
public class LoopDiagnosticsContextTests
{
    [TestMethod]
    public void Value_DefaultsToNull()
    {
        Assert.IsNull(LoopDiagnosticsContext.Value);
    }

    [TestMethod]
    public void Set_BindsInstanceForCurrentAsyncFlow()
    {
        var diag = new LoopDiagnostics();
        using var _ = LoopDiagnosticsContext.Set(diag);

        Assert.AreSame(diag, LoopDiagnosticsContext.Value);
    }

    [TestMethod]
    public void Dispose_RestoresPreviousBinding()
    {
        var outer = new LoopDiagnostics { Iterations = 1 };
        using (LoopDiagnosticsContext.Set(outer))
        {
            Assert.AreSame(outer, LoopDiagnosticsContext.Value);

            var inner = new LoopDiagnostics { Iterations = 2 };
            using (LoopDiagnosticsContext.Set(inner))
            {
                Assert.AreSame(inner, LoopDiagnosticsContext.Value);
            }

            Assert.AreSame(outer, LoopDiagnosticsContext.Value);
        }

        Assert.IsNull(LoopDiagnosticsContext.Value);
    }

    [TestMethod]
    public async Task ConcurrentAsyncFlows_DoNotShareDiagnostics()
    {
        // Two concurrent tasks each bind their own diagnostics. AsyncLocal must
        // give each task its own view, otherwise subagent A's failure handler
        // would see subagent B's last tool call.
        async Task<int> RunFlow(int marker)
        {
            var diag = new LoopDiagnostics();
            using var _ = LoopDiagnosticsContext.Set(diag);
            await Task.Yield();

            // The handle inside this async flow must be the one we just set.
            Assert.AreSame(diag, LoopDiagnosticsContext.Value);

            LoopDiagnosticsContext.Value!.Iterations = marker;
            await Task.Delay(10);
            return LoopDiagnosticsContext.Value.Iterations;
        }

        var a = RunFlow(101);
        var b = RunFlow(202);

        var results = await Task.WhenAll(a, b);
        Assert.AreEqual(101, results[0]);
        Assert.AreEqual(202, results[1]);
        Assert.IsNull(LoopDiagnosticsContext.Value);
    }
}

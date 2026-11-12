using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests;

/// <summary>Copilot tool calls are paired by the SDK's call id (#795).</summary>
[TestClass]
public sealed class CopilotSessionEventsBridgeTests
{
    private static (CopilotSessionEventsBridge Bridge, RecordingNotifier Notifier) Create()
    {
        var notifier = new RecordingNotifier();
        var bridge = new CopilotSessionEventsBridge();
        bridge.SetServiceProvider(new ServiceCollection()
            .AddSingleton<IToolProgressNotifier>(notifier)
            .BuildServiceProvider());
        return (bridge, notifier);
    }

    [TestMethod]
    public async Task Completion_IsPairedWithItsStart_ByCallId()
    {
        var (bridge, notifier) = Create();

        // Two calls to the same tool, completing out of order.
        await bridge.OnToolStartAsync("call-a", "bash", "ls", CancellationToken.None);
        await bridge.OnToolStartAsync("call-b", "bash", "pwd", CancellationToken.None);
        await bridge.OnToolCompleteAsync("call-b", success: true, "/home", CancellationToken.None);
        await bridge.OnToolCompleteAsync("call-a", success: false, "permission denied", CancellationToken.None);

        var b = notifier.Calls.Single(c => c.ToolCallId == "call-b" && c.Phase != ToolCallPhase.Started);
        Assert.AreEqual(ToolCallPhase.Finished, b.Phase);
        Assert.AreEqual("bash", b.ToolCallName, "the completion carries only the id; the name comes from the start");
        Assert.IsNotNull(b.DurationMs);

        var a = notifier.Calls.Single(c => c.ToolCallId == "call-a" && c.Phase != ToolCallPhase.Started);
        Assert.AreEqual(ToolCallPhase.Failed, a.Phase);
        Assert.AreEqual("permission denied", a.Error);
        Assert.AreEqual("ls", notifier.Calls.Single(c => c.ToolCallId == "call-a" && c.Phase == ToolCallPhase.Started).ArgsSummary);

        Assert.AreEqual(0, bridge.InFlightCount, "completed calls are forgotten");
    }

    [TestMethod]
    public async Task Completion_WithoutAKnownStart_StillReports()
    {
        var (bridge, notifier) = Create();

        await bridge.OnToolCompleteAsync("call-x", success: true, "ok", CancellationToken.None);

        var call = notifier.Calls.Single();
        Assert.AreEqual("call-x", call.ToolCallId);
        Assert.IsNull(call.DurationMs);
    }

    private sealed class RecordingNotifier : IToolProgressNotifier
    {
        public List<ToolCallInfo> Calls { get; } = [];

        public Task OnToolInvokingAsync(string toolName, string? argsSummary, CancellationToken ct) => Task.CompletedTask;
        public Task OnToolInvokedAsync(string toolName, string? resultSummary, CancellationToken ct) => Task.CompletedTask;

        public Task OnToolCallAsync(ToolCallInfo info, CancellationToken ct)
        {
            Calls.Add(info);
            return Task.CompletedTask;
        }
    }
}

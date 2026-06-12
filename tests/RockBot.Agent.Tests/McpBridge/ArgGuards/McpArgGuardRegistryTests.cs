using System.Text.Json;
using RockBot.Agent.McpBridge.ArgGuards;

namespace RockBot.Agent.Tests.McpBridge.ArgGuards;

[TestClass]
public class McpArgGuardRegistryTests
{
    private sealed class NoopGuard : IMcpArgGuard
    {
        public void ValidateOptions(JsonElement? options) { }
        public ValueTask<McpArgGuardResult> ApplyAsync(McpArgGuardContext context, CancellationToken ct) =>
            ValueTask.FromResult(McpArgGuardResult.Allowed);
    }

    [TestMethod]
    public void Get_RegisteredHandler_ReturnsInstance()
    {
        var guard = new NoopGuard();
        var registry = new McpArgGuardRegistry([new McpArgGuardRegistration("path-prefix", guard)]);
        Assert.AreSame(guard, registry.Get("path-prefix"));
    }

    [TestMethod]
    public void Get_HandlerNameDiffersByCase_ReturnsInstance()
    {
        var guard = new NoopGuard();
        var registry = new McpArgGuardRegistry([new McpArgGuardRegistration("path-prefix", guard)]);
        Assert.AreSame(guard, registry.Get("Path-Prefix"));
    }

    [TestMethod]
    public void Get_UnknownHandler_ThrowsKeyNotFoundListingKnownHandlers()
    {
        var registry = new McpArgGuardRegistry([new McpArgGuardRegistration("path-prefix", new NoopGuard())]);
        var ex = Assert.ThrowsExactly<KeyNotFoundException>(() => registry.Get("nope"));
        StringAssert.Contains(ex.Message, "nope");
        StringAssert.Contains(ex.Message, "path-prefix");
    }

    [TestMethod]
    public void Contains_KnownAndUnknown_ReportCorrectly()
    {
        var registry = new McpArgGuardRegistry([new McpArgGuardRegistration("path-prefix", new NoopGuard())]);
        Assert.IsTrue(registry.Contains("path-prefix"));
        Assert.IsFalse(registry.Contains("nope"));
    }
}

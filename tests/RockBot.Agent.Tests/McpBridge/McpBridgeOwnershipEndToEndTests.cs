using System.Text.Json;
using RockBot.Agent.McpBridge;
using RockBot.Tools.Mcp.Elicitation;
using static RockBot.Agent.Tests.McpBridge.McpWrapperEndToEndTests;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// Operator policy versus the LLM-callable <c>mcp_register_server</c> / <c>mcp_unregister_server</c>
/// (#603). The model can add servers and remove the ones it added. It can't replace or remove an
/// entry the operator configured, so it can't shed that entry's policy or re-point its credentials.
/// </summary>
[TestClass]
public class McpBridgeOwnershipEndToEndTests
{
    /// <summary>An operator's entry: tool filter, credentials, elicitation policy and a timeout.</summary>
    private static void OperatorPolicy(McpBridgeServerConfig entry)
    {
        entry.DeniedTools = ["delete_everything"];
        entry.Headers = new() { ["X-Api-Key"] = "operator-secret" };
        entry.Elicitation = new McpElicitationConfig { Mode = "off", DeniedFields = ["accountId"] };
        entry.ToolTimeoutMs = 7_000;
    }

    private static Task<BridgeHarness> StartWithOperatorServerAsync() =>
        BridgeHarness.StartAsync([SendEmail(new Counter()), DeleteEverything()], configure: OperatorPolicy);

    private static string Snapshot(McpBridgeServerConfig entry) => JsonSerializer.Serialize(entry);

    [TestMethod]
    public async Task ReRegistering_AnOperatorServer_IsRefused_AndCantReExposeADeniedTool()
    {
        await using var harness = await StartWithOperatorServerAsync();
        var before = Snapshot(harness.ReadPersistedConfig().McpServers[BridgeHarness.ServerName]);

        var response = await harness.RegisterAsync(BridgeHarness.ServerName, harness.ServerUrl);

        Assert.IsFalse(response.Success);
        StringAssert.Contains(response.Error, "managed by the operator");
        Assert.AreEqual(before, Snapshot(harness.ReadPersistedConfig().McpServers[BridgeHarness.ServerName]));

        var denied = await harness.InvokeAsync("delete_everything", "{}");
        Assert.IsTrue(denied.IsError, "The operator-denied tool must stay unavailable.");
    }

    [TestMethod]
    public async Task ReRegistering_AnOperatorServer_AtAnotherUrl_LeavesItsEndpointAndCredentials()
    {
        await using var harness = await StartWithOperatorServerAsync();

        var response = await harness.RegisterAsync(BridgeHarness.ServerName, "https://attacker.example/mcp");

        Assert.IsFalse(response.Success);
        var entry = harness.ReadPersistedConfig().McpServers[BridgeHarness.ServerName];
        Assert.AreEqual(harness.ServerUrl, entry.Url);
        Assert.AreEqual("operator-secret", entry.Headers["X-Api-Key"]);
    }

    [TestMethod]
    public async Task Unregistering_AnOperatorServer_IsRefused_AndItsPolicyStays()
    {
        await using var harness = await StartWithOperatorServerAsync();
        var before = Snapshot(harness.ReadPersistedConfig().McpServers[BridgeHarness.ServerName]);

        var unregister = await harness.UnregisterAsync(BridgeHarness.ServerName);
        var register = await harness.RegisterAsync(BridgeHarness.ServerName, harness.ServerUrl);

        Assert.IsFalse(unregister.Success);
        StringAssert.Contains(unregister.Error, "managed by the operator");
        Assert.IsFalse(register.Success);
        Assert.AreEqual(before, Snapshot(harness.ReadPersistedConfig().McpServers[BridgeHarness.ServerName]),
            "Unregister-then-register must not shed tool filters, credentials, elicitation policy or the timeout.");

        var stillServed = await harness.InvokeAsync("send_email", """{"to":["a@b.c"],"subject":"hi"}""");
        Assert.IsFalse(stillServed.IsError, stillServed.Content);
    }

    [TestMethod]
    public async Task AgentRegisteredServer_GetsNoOperatorPolicy_AndTheAgentCanRemoveAndReAddIt()
    {
        await using var harness = await StartWithOperatorServerAsync();

        // Same URL as the operator's entry, which differs by its credentials and filters — so this
        // is a separate entry, and the operator's credentials don't follow the URL into it.
        var added = await harness.RegisterAsync("extra", harness.ServerUrl);
        Assert.IsTrue(added.Success, added.Error);

        var entry = harness.ReadPersistedConfig().McpServers["extra"];
        Assert.AreEqual(McpBridgeServerConfig.AgentOrigin, entry.Origin);
        Assert.IsFalse(entry.HasOperatorPolicy());
        Assert.AreEqual(0, entry.Headers.Count);

        var again = await harness.RegisterAsync("extra", harness.ServerUrl);
        Assert.IsFalse(again.Success, "An existing name is never replaced, even the agent's own.");
        StringAssert.Contains(again.Error, "mcp_unregister_server");

        var removed = await harness.UnregisterAsync("extra");
        Assert.IsTrue(removed.Success, removed.Error);
        Assert.IsFalse(harness.ReadPersistedConfig().McpServers.ContainsKey("extra"));

        var readded = await harness.RegisterAsync("extra", harness.ServerUrl);
        Assert.IsTrue(readded.Success, readded.Error);
    }

    [TestMethod]
    public async Task AgentRegisteredServer_OnceTheOperatorAddsPolicy_IsTheOperators()
    {
        await using var harness = await StartWithOperatorServerAsync();
        Assert.IsTrue((await harness.RegisterAsync("extra", harness.ServerUrl)).Success);

        harness.EditPersistedEntry("extra", e => e.DeniedTools = ["delete_everything"]);
        await harness.RestartBridgeAsync();

        var removed = await harness.UnregisterAsync("extra");

        Assert.IsFalse(removed.Success);
        StringAssert.Contains(removed.Error, "managed by the operator");
        CollectionAssert.AreEqual(new[] { "delete_everything" },
            harness.ReadPersistedConfig().McpServers["extra"].DeniedTools);
    }

    [TestMethod]
    public async Task Unregistering_AnUnknownServer_SaysSo()
    {
        await using var harness = await StartWithOperatorServerAsync();

        var response = await harness.UnregisterAsync("nope");

        Assert.IsFalse(response.Success);
        StringAssert.Contains(response.Error, "nope");
    }
}

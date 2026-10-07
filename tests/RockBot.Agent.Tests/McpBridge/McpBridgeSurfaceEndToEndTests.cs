using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using RockBot.Host;
using RockBot.Tools.Mcp;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// End-to-end coverage of server identity and surface change detection: stable ids that survive
/// restarts, fingerprints in the published index, refreshes that publish only real changes, and
/// summaries that aren't regenerated for an unchanged server.
/// </summary>
[TestClass]
public class McpBridgeSurfaceEndToEndTests
{
    private static McpServerTool ListAccounts() => McpServerTool.Create(
        () => "work, personal",
        new McpServerToolCreateOptions { Name = "list_accounts", Description = "Lists accounts." });

    private static McpServerTool SendEmail() => McpServerTool.Create(
        (string to, string subject) => $"sent to {to}",
        new McpServerToolCreateOptions { Name = "send_email", Description = "Sends an email." });

    private static McpServerSummary LastSummary(BridgeHarness harness) =>
        harness.IndexMessages.Last(m => m.Servers.Count > 0).Servers.Single();

    private static string? PersistedId(BridgeHarness harness)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(harness.ConfigPath));
        return doc.RootElement.GetProperty("mcpServers").GetProperty(BridgeHarness.ServerName)
            .TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    [TestMethod]
    public async Task ConnectedServer_IsPublishedWithIdAndFingerprints()
    {
        await using var harness = await BridgeHarness.StartAsync([ListAccounts(), SendEmail()]);

        var summary = LastSummary(harness);

        Assert.IsNotNull(summary.ServerId);
        Assert.AreEqual(12, summary.ServerId.Length);
        Assert.AreEqual(PersistedId(harness), summary.ServerId, "The id must be the one persisted in mcp.json.");
        Assert.AreEqual(64, summary.Fingerprint?.Length);
        CollectionAssert.AreEquivalent(new[] { "list_accounts", "send_email" }, summary.ToolFingerprints.Keys.ToArray());
    }

    [TestMethod]
    public async Task ServerId_SurvivesARestart()
    {
        await using var harness = await BridgeHarness.StartAsync([ListAccounts()]);
        var firstId = LastSummary(harness).ServerId;

        await harness.RestartBridgeAsync();

        Assert.AreEqual(firstId, LastSummary(harness).ServerId);
        Assert.AreEqual(firstId, PersistedId(harness));
    }

    [TestMethod]
    public async Task ReRegisteringTheSameEndpoint_KeepsTheId()
    {
        await using var harness = await BridgeHarness.StartAsync([ListAccounts()]);
        var firstId = LastSummary(harness).ServerId;

        var response = await harness.RegisterAsync(BridgeHarness.ServerName, harness.ServerUrl);

        Assert.IsTrue(response.Success, response.Error);
        Assert.AreEqual(firstId, LastSummary(harness).ServerId);
    }

    [TestMethod]
    [DataRow("adjutant__mail")]
    [DataRow("-leading-dash")]
    [DataRow("has space")]
    public async Task Register_RejectsNamesThatCantBecomeToolNames(string name)
    {
        await using var harness = await BridgeHarness.StartAsync([ListAccounts()]);

        var response = await harness.RegisterAsync(name, harness.ServerUrl);

        Assert.IsFalse(response.Success);
        StringAssert.Contains(response.Error, "invalid");
    }

    [TestMethod]
    public async Task Refresh_OfAnUnchangedSurface_PublishesNothing()
    {
        await using var harness = await BridgeHarness.StartAsync([ListAccounts()]);
        var published = harness.IndexMessages.Count;

        var changed = await harness.Bridge.RefreshSurfaceAsync(BridgeHarness.ServerName, CancellationToken.None);

        Assert.IsFalse(changed);
        Assert.AreEqual(published, harness.IndexMessages.Count);
    }

    [TestMethod]
    public async Task Refresh_AfterTheServerAddsATool_PublishesTheNewSurface()
    {
        await using var harness = await BridgeHarness.StartAsync([ListAccounts()]);
        var before = LastSummary(harness);

        harness.AddServerTool(SendEmail());
        var changed = await harness.Bridge.RefreshSurfaceAsync(BridgeHarness.ServerName, CancellationToken.None);

        Assert.IsTrue(changed);
        var after = LastSummary(harness);
        Assert.AreNotEqual(before.Fingerprint, after.Fingerprint);
        CollectionAssert.Contains(after.ToolNames, "send_email");
        Assert.AreEqual(before.ServerId, after.ServerId);
        Assert.AreEqual(before.ToolFingerprints["list_accounts"], after.ToolFingerprints["list_accounts"],
            "An untouched tool keeps its fingerprint.");

        var invoke = await harness.InvokeAsync("send_email", """{"to":"a@b.c","subject":"hi"}""");
        Assert.IsFalse(invoke.IsError, invoke.Content);
    }

    [TestMethod]
    public async Task Reconnect_OfAnUnchangedServer_ReusesTheSummary()
    {
        var llm = new CountingLlmClient();
        await using var harness = await BridgeHarness.StartAsync([ListAccounts()], llmClient: llm);
        Assert.AreEqual(1, llm.Calls);

        var response = await harness.RegisterAsync(BridgeHarness.ServerName, harness.ServerUrl);

        Assert.IsTrue(response.Success, response.Error);
        Assert.AreEqual(1, llm.Calls, "An unchanged server must not cost another summary call.");
        Assert.AreEqual("A summary.", LastSummary(harness).Summary);
    }

    [TestMethod]
    public async Task Refresh_OfAChangedSurface_RegeneratesTheSummary()
    {
        var llm = new CountingLlmClient();
        await using var harness = await BridgeHarness.StartAsync([ListAccounts()], llmClient: llm);

        harness.AddServerTool(SendEmail());
        await harness.Bridge.RefreshSurfaceAsync(BridgeHarness.ServerName, CancellationToken.None);

        Assert.AreEqual(2, llm.Calls);
    }

    private sealed class CountingLlmClient : ILlmClient
    {
        private int _calls;
        public int Calls => _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "A summary.")));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options, CancellationToken cancellationToken) =>
            GetResponseAsync(messages, options, cancellationToken);
    }
}

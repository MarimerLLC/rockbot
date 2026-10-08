using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RockBot.Tools;
using RockBot.Tools.Mcp;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// Downstream MCP resources (#617), end to end against a real MCP server: discovery, reading
/// concrete and template-matched URIs, the unknown-URI hint, blobs and large text going to the
/// shared volume instead of into context, resource blocks in tool results, and a tools-only server
/// staying quiet. See <see cref="BridgeHarness"/>.
/// </summary>
[TestClass]
public class McpBridgeResourceTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private static McpServerResource Readme() => McpServerResource.Create(
        () => "# Readme",
        new McpServerResourceCreateOptions
        {
            UriTemplate = "docs://readme", Name = "readme", Description = "The project readme", MimeType = "text/markdown"
        });

    private static McpServerResource Files() => McpServerResource.Create(
        (string name) => $"contents of {name}",
        new McpServerResourceCreateOptions { UriTemplate = "docs://files/{name}", Name = "files", MimeType = "text/plain" });

    private static McpServerResource Logo() => McpServerResource.Create(
        () => (ResourceContents)BlobResourceContents.FromBytes(PngBytes, "img://logo.png", "image/png"),
        new McpServerResourceCreateOptions { UriTemplate = "img://logo.png", Name = "logo", MimeType = "image/png" });

    private static McpServerResource Big() => McpServerResource.Create(
        () => new string('x', 40_000),
        new McpServerResourceCreateOptions { UriTemplate = "docs://big", Name = "big", MimeType = "text/plain" });

    private static McpServerTool Echo() => McpServerTool.Create(
        (string text) => text,
        new McpServerToolCreateOptions { Name = "echo" });

    /// <summary>A tool whose result carries a resource link, an embedded text resource and an embedded blob.</summary>
    private static McpServerTool Linky() => McpServerTool.Create(
        () => new CallToolResult
        {
            Content =
            [
                new ResourceLinkBlock { Uri = "docs://files/report.md", Name = "report.md", MimeType = "text/plain" },
                new EmbeddedResourceBlock
                {
                    Resource = new TextResourceContents { Uri = "docs://readme", MimeType = "text/markdown", Text = "# Inline readme" }
                },
                new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(PngBytes, "img://logo.png", "image/png") },
            ]
        },
        new McpServerToolCreateOptions { Name = "linky" });

    private static Task<BridgeHarness> StartAsync() =>
        BridgeHarness.StartAsync([Echo(), Linky()], resources: [Readme(), Files(), Logo(), Big()]);

    // ── Discovery ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ListResources_ReturnsResourcesAndTemplates()
    {
        await using var harness = await StartAsync();

        var response = await harness.ListResourcesAsync();

        Assert.IsNull(response.Error, response.Error);
        CollectionAssert.AreEquivalent(
            new[] { "docs://readme", "img://logo.png", "docs://big" },
            response.Resources.Select(r => r.Uri).ToArray());
        Assert.AreEqual("docs://files/{name}", response.Templates.Single().Uri);
        Assert.IsTrue(response.Templates.Single().IsTemplate);

        var readme = response.Resources.Single(r => r.Uri == "docs://readme");
        Assert.AreEqual("readme", readme.Name);
        Assert.AreEqual("The project readme", readme.Description);
        Assert.AreEqual("text/markdown", readme.MimeType);
    }

    [TestMethod]
    public async Task Summary_CountsAndNamesTheResources()
    {
        await using var harness = await StartAsync();

        var summary = harness.IndexMessages.Last().Servers.Single(s => s.ServerName == BridgeHarness.ServerName);

        Assert.AreEqual(4, summary.ResourceCount);
        CollectionAssert.IsSubsetOf(new[] { "readme", "files", "logo", "big" }, summary.ResourceNames);
        StringAssert.Contains(summary.Summary, "4 resource(s)");
    }

    [TestMethod]
    public async Task ToolsOnlyServer_HasNoResources_AndLogsNothingAboutThemAboveDebug()
    {
        await using var harness = await BridgeHarness.StartAsync([Echo()]);

        var list = await harness.ListResourcesAsync();
        var read = await harness.ReadResourceAsync("docs://readme");

        Assert.IsNull(list.Error, list.Error);
        Assert.AreEqual(0, list.Resources.Count + list.Templates.Count);
        Assert.AreEqual(0, harness.IndexMessages.Last().Servers.Single().ResourceCount);
        StringAssert.Contains(read.Error, "exposes no resources");
        Assert.IsFalse(harness.BridgeLog.Any(e => e.Level >= LogLevel.Warning),
            "unexpected warnings: " + string.Join(" | ", harness.BridgeLog.Where(e => e.Level >= LogLevel.Warning).Select(e => e.Message)));
    }

    [TestMethod]
    public async Task ServerAdvertisingNoResources_ListsNone_WithoutWarnings()
    {
        // The live OneDrive servers' shape: the capability is there, the lists are empty.
        await using var harness = await BridgeHarness.StartAsync([Echo()], resources: []);

        var list = await harness.ListResourcesAsync();

        Assert.IsNull(list.Error, list.Error);
        Assert.AreEqual(0, list.Resources.Count + list.Templates.Count);
        Assert.IsFalse(harness.BridgeLog.Any(e => e.Level >= LogLevel.Warning));
    }

    [TestMethod]
    public async Task NoResources_LeavesTheSurfaceFingerprintAsItWas()
    {
        await using var withoutCapability = await BridgeHarness.StartAsync([Echo()]);
        await using var withEmptyCapability = await BridgeHarness.StartAsync([Echo()], resources: []);

        Assert.AreEqual(
            withoutCapability.IndexMessages.Last().Servers.Single().Fingerprint,
            withEmptyCapability.IndexMessages.Last().Servers.Single().Fingerprint);
    }

    [TestMethod]
    public async Task ResourceAddedAtRuntime_IsListedAfterARefresh_AndMovesTheFingerprint()
    {
        await using var harness = await BridgeHarness.StartAsync([Echo()], resources: [Readme()]);
        var before = harness.IndexMessages.Last().Servers.Single().Fingerprint;

        harness.AddServerResource(Files());
        await harness.Bridge.RefreshSurfaceAsync(BridgeHarness.ServerName, CancellationToken.None);

        var list = await harness.ListResourcesAsync();
        Assert.AreEqual("docs://files/{name}", list.Templates.Single().Uri);
        var after = harness.IndexMessages.Last().Servers.Single().Fingerprint;
        Assert.IsNotNull(after);
        Assert.AreNotEqual(before, after);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ReadResource_ConcreteUri_ReturnsTheTextInline()
    {
        await using var harness = await StartAsync();

        var response = await harness.ReadResourceAsync("docs://readme");

        Assert.IsNull(response.Error, response.Error);
        var content = response.Contents.Single();
        Assert.AreEqual("# Readme", content.Text);
        Assert.AreEqual("docs://readme", content.Uri);
        Assert.IsNull(content.Path);
    }

    [TestMethod]
    public async Task ReadResource_TemplateMatchedUri_ReadsTheExpansion()
    {
        await using var harness = await StartAsync();

        var response = await harness.ReadResourceAsync("docs://files/a.md");

        Assert.IsNull(response.Error, response.Error);
        Assert.AreEqual("contents of a.md", response.Contents.Single().Text);
    }

    [TestMethod]
    public async Task ReadResource_UnknownUri_HintsWithTheDeclaredUrisAndTemplates()
    {
        await using var harness = await StartAsync();

        var response = await harness.ReadResourceAsync("docs://nope/at/all");

        Assert.IsNotNull(response.Error);
        StringAssert.Contains(response.Error, "Unknown resource 'docs://nope/at/all' on server 'fixture'");
        StringAssert.Contains(response.Error, "docs://readme");
        StringAssert.Contains(response.Error, "docs://files/{name}");
        StringAssert.Contains(response.Error, "img://logo.png");
        StringAssert.Contains(response.Error, "mcp_list_resources(server_name: \"fixture\")");
    }

    [TestMethod]
    public async Task ReadResource_UnknownServer_ListsTheRegisteredServers()
    {
        await using var harness = await StartAsync();

        var response = await harness.ReadResourceAsync("docs://readme", server: "nope");

        StringAssert.Contains(response.Error, "Unknown MCP server 'nope'");
        StringAssert.Contains(response.Error, "fixture");
    }

    [TestMethod]
    public async Task ReadResource_Blob_ArrivesThroughTheSharedVolume_NotAsBase64()
    {
        await using var harness = await StartAsync();

        var response = await harness.ReadResourceAsync("img://logo.png");

        Assert.IsNull(response.Error, response.Error);
        var content = response.Contents.Single();
        Assert.IsNull(content.Text);
        Assert.IsNotNull(content.Path);
        StringAssert.StartsWith(content.Path, harness.AttachmentsPath);
        StringAssert.EndsWith(content.Path, ".png");
        Assert.AreEqual(PngBytes.Length, content.Size);
        CollectionAssert.AreEqual(PngBytes, await File.ReadAllBytesAsync(content.Path));

        var json = JsonSerializer.Serialize(response);
        Assert.IsFalse(json.Contains(Convert.ToBase64String(PngBytes)), "the blob must not travel inline");
    }

    [TestMethod]
    public async Task ReadResource_TextOverTheInlineLimit_IsSavedToTheSharedVolume()
    {
        await using var harness = await StartAsync();

        var response = await harness.ReadResourceAsync("docs://big");

        var content = response.Contents.Single();
        Assert.IsNull(content.Text);
        Assert.IsNotNull(content.Path);
        Assert.AreEqual(40_000, (await File.ReadAllTextAsync(content.Path)).Length);
    }

    [TestMethod]
    public async Task ReadResource_InlineLimitIsConfigurable()
    {
        await using var harness = await BridgeHarness.StartAsync([Echo()], resources: [Big()],
            configureBridge: o => o.ResourceInlineTextLimit = 50_000);

        var response = await harness.ReadResourceAsync("docs://big");

        Assert.AreEqual(40_000, response.Contents.Single().Text?.Length);
    }

    // ── Resource blocks in tool results ──────────────────────────────────────

    [TestMethod]
    public async Task ToolResult_ResourceBlocks_AreRenderedMeaningfully()
    {
        await using var harness = await StartAsync();

        var response = await harness.InvokeAsync("linky", "{}");

        Assert.IsFalse(response.IsError, response.Content);
        StringAssert.Contains(response.Content,
            "[resource link] report.md: docs://files/report.md (text/plain). " +
            "Read it with mcp_read_resource(server_name: \"fixture\", uri: \"docs://files/report.md\").");
        StringAssert.Contains(response.Content, "[resource docs://readme (text/markdown)]\n# Inline readme");
        Assert.IsFalse(response.Content!.Contains(Convert.ToBase64String(PngBytes)), "the blob must not travel inline");

        // The embedded blob was captured like an image: a descriptor naming a saved file.
        var descriptor = response.ContentBlocks!
            .Select(b => b.Text)
            .Single(t => t is not null && t.Contains("\"path\""))!;
        using var doc = JsonDocument.Parse(descriptor);
        Assert.AreEqual("img://logo.png", doc.RootElement.GetProperty("uri").GetString());
        CollectionAssert.AreEqual(PngBytes, await File.ReadAllBytesAsync(doc.RootElement.GetProperty("path").GetString()!));
    }

    // ── Through the agent's management tools ─────────────────────────────────

    [TestMethod]
    public async Task ManagementTools_ListAndRead_ThroughTheAgent()
    {
        await using var harness = await StartAsync();
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness);

        var services = await CallAsync(agent, "mcp_list_services", "{}");
        var list = await CallAsync(agent, "mcp_list_resources", """{"server_name":"fixture"}""");
        var read = await CallAsync(agent, "mcp_read_resource", """{"server_name":"fixture","uri":"docs://files/x.txt"}""");
        var unknown = await CallAsync(agent, "mcp_read_resource", """{"server_name":"fixture","uri":"docs://nope/x"}""");
        var details = await CallAsync(agent, "mcp_get_service_details", """{"server_name":"fixture"}""");

        StringAssert.Contains(services.Content, "\"resourceCount\":4");
        Assert.IsFalse(list.IsError, list.Content);
        StringAssert.Contains(list.Content, "\"uriTemplate\":\"docs://files/{name}\"");
        StringAssert.Contains(list.Content, "\"uri\":\"docs://readme\"");
        Assert.IsFalse(read.IsError, read.Content);
        StringAssert.Contains(read.Content, "\"text\":\"contents of x.txt\"");
        Assert.IsTrue(unknown.IsError);
        StringAssert.Contains(unknown.Content, "docs://files/{name}");
        StringAssert.Contains(details.Content, "lists 4 resource(s)");
    }

    [TestMethod]
    public async Task ManagementTools_ToolsOnlyServer_SaysSo()
    {
        await using var harness = await BridgeHarness.StartAsync([Echo()]);
        var agent = await McpWrapperEndToEndTests.ConnectAgentAsync(harness);

        var services = await CallAsync(agent, "mcp_list_services", "{}");
        var list = await CallAsync(agent, "mcp_list_resources", """{"server_name":"fixture"}""");
        var details = await CallAsync(agent, "mcp_get_service_details", """{"server_name":"fixture"}""");

        Assert.IsFalse(services.Content!.Contains("resource", StringComparison.OrdinalIgnoreCase), services.Content);
        Assert.IsFalse(list.IsError, list.Content);
        Assert.AreEqual("MCP server 'fixture' lists no resources or resource templates.", list.Content);
        Assert.IsFalse(details.Content!.Contains("resource(s)"), details.Content);
    }

    private static Task<ToolInvokeResponse> CallAsync(McpWrapperEndToEndTests.AgentSide agent, string tool, string arguments) =>
        agent.Registry.GetExecutor(tool)!.ExecuteAsync(
            new ToolInvokeRequest { ToolCallId = Guid.NewGuid().ToString("N"), ToolName = tool, Arguments = arguments },
            CancellationToken.None);
}

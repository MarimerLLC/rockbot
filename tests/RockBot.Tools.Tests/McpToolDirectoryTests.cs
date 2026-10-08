using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests;

/// <summary>
/// <see cref="McpToolDirectory"/> (#647): tools resolve by server and tool or by typed name (looked
/// up, never parsed), a tool without a wrapper still resolves, and fingerprints are the bridge's.
/// Pinned mode is the point: there the registry holds no wrapper at all.
/// </summary>
[TestClass]
public class McpToolDirectoryTests
{
    private readonly Dictionary<string, IReadOnlyList<McpToolDefinition>?> _serverTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolRegistry _registry = new();

    private async Task<(McpToolDirectory Directory, McpServerIndex Index)> BuildAsync()
    {
        _serverTools["microsoft.learn"] =
        [
            new McpToolDefinition { Name = "docs.search", Description = "Searches docs." },
            new McpToolDefinition { Name = new string('t', 70), Description = "Name too long for a wrapper." }
        ];

        var cache = ToolSchemaCache.WithPrompts((server, _) => Task.FromResult(_serverTools.GetValueOrDefault(server) is { } tools
            ? new McpServerSurface(tools, [new McpPromptDefinition { Name = "daily_briefing" }])
            : null));
        var identity = new AgentIdentity("test-agent");
        var publisher = new TrackingPublisher();
        var proxy = new McpToolProxy(publisher, new StubSubscriber(), identity, NullLogger<McpToolProxy>.Instance);
        var index = new McpServerIndex();
        var management = new McpManagementExecutor(index, proxy, publisher, new StubSubscriber(), identity,
            NullLogger<McpManagementExecutor>.Instance);
        var catalog = new McpWrapperCatalog(_registry, cache, management,
            Options.Create(new McpToolSurfaceOptions { WrapperMode = McpWrapperMode.Pinned }),
            NullLogger<McpWrapperCatalog>.Instance);

        var summary = new McpServerSummary
        {
            ServerName = "microsoft.learn",
            ServerId = "abc123def456",
            Fingerprint = "fp-server",
            ToolNames = ["docs.search", new string('t', 70)],
            ToolFingerprints = new() { ["docs.search"] = "fp-search", [new string('t', 70)] = "fp-long" }
        };
        var indexed = new McpServersIndexed { Servers = [summary] };
        index.Apply(indexed);
        await catalog.ApplyAsync(indexed, CancellationToken.None);

        return (new McpToolDirectory(catalog, index), index);
    }

    [TestMethod]
    public async Task Resolves_ByServerAndTool_InPinnedMode_WhereTheRegistryHasNoWrapper()
    {
        var (directory, _) = await BuildAsync();

        var entry = directory.Resolve("microsoft.learn", "docs.search");

        Assert.AreEqual(0, _registry.GetTools().Count, "pinned mode registers no wrapper");
        Assert.IsNotNull(entry?.Wrapper);
        Assert.AreEqual("microsoft-learn__docs-search", entry.Wrapper.Name);
        Assert.AreEqual("docs.search", entry.Wrapper.DownstreamName);
        Assert.AreEqual("fp-search", entry.Fingerprint, "the bridge's per-tool fingerprint");
    }

    [TestMethod]
    public async Task APromptsTypedName_DoesNotResolveAsATool()
    {
        var (directory, _) = await BuildAsync();

        Assert.IsNull(directory.Resolve(null, "microsoft-learn__daily_briefing-prompt"));
        Assert.IsNull(directory.Resolve("microsoft.learn", "daily_briefing"));
        Assert.IsFalse(directory.ForServer("microsoft.learn").Any(e => e.ToolName == "daily_briefing"));
    }

    [TestMethod]
    public async Task Resolves_ByTypedName_WithoutParsingIt()
    {
        var (directory, _) = await BuildAsync();

        // The sanitised name can't be split back into "microsoft.learn" / "docs.search".
        var entry = directory.Resolve(null, "microsoft-learn__docs-search");

        Assert.AreEqual("microsoft.learn", entry?.ServerName);
        Assert.AreEqual("docs.search", entry?.ToolName);
        Assert.IsNull(directory.Resolve(null, "microsoft.learn__docs.search"), "only real typed names resolve");
    }

    [TestMethod]
    public async Task ToolWithoutAWrapper_StillResolves_WithNoWrapper()
    {
        var (directory, _) = await BuildAsync();

        var entry = directory.Resolve("microsoft.learn", new string('t', 70));

        Assert.IsNotNull(entry);
        Assert.IsNull(entry.Wrapper);
        Assert.AreEqual("fp-long", entry.Fingerprint);
    }

    [TestMethod]
    public async Task ForServer_ListsTheWrappedTools_AndIndexingIsReported()
    {
        var (directory, _) = await BuildAsync();

        CollectionAssert.AreEqual(new[] { "docs.search" }, directory.ForServer("Microsoft.Learn").Select(e => e.ToolName).ToArray());
        Assert.IsTrue(directory.IsServerIndexed("microsoft.learn"));
        Assert.IsFalse(directory.IsServerIndexed("adjutant"));
        Assert.IsNull(directory.Resolve("microsoft.learn", "gone"));
        Assert.IsNull(directory.Resolve("adjutant", "send_email"));
    }
}

/// <summary>
/// <see cref="WispToolFingerprints"/>: which tools a wisp definition calls, and their fingerprints
/// as the directory knows them now.
/// </summary>
[TestClass]
public class WispToolFingerprintsTests
{
    private const string Definition = """
        {
          "description": "d",
          "tools": ["calendar-mcp__list_accounts", "web_browse"],
          "steps": [
            { "id": "a", "mode": "Direct", "gateway": "Mcp", "server": "calendar-mcp", "tool": "get_events" },
            { "id": "b", "mode": "Direct", "gateway": "mcp", "tool": "ms365__search_emails" },
            { "id": "c", "mode": "Direct", "gateway": "Web", "tool": "web_search" },
            { "id": "d", "mode": "Llm", "prompt": "summarise" }
          ]
        }
        """;

    [TestMethod]
    public void References_ListsMcpStepsAndToolsEntries_AsWritten()
    {
        var refs = WispToolFingerprints.References(Definition);

        CollectionAssert.AreEquivalent(
            new (string?, string)[]
            {
                ("calendar-mcp", "get_events"),
                (null, "ms365__search_emails"),
                (null, "calendar-mcp__list_accounts"),
                (null, "web_browse")
            },
            refs.ToArray());
    }

    [TestMethod]
    public void Capture_RecordsWhatResolves_KeyedServerSlashTool()
    {
        var directory = new StubDirectory(new()
        {
            [("calendar-mcp", "get_events")] = new McpToolEntry("calendar-mcp", "get_events", null, "fp-events"),
            [(null, "ms365__search_emails")] = new McpToolEntry("ms365", "search_emails", null, "fp-search"),
            [(null, "calendar-mcp__list_accounts")] = new McpToolEntry("calendar-mcp", "list_accounts", null, null)
        });

        var map = WispToolFingerprints.Capture(Definition, directory)!;

        Assert.AreEqual(2, map.Count, "web_browse doesn't resolve and list_accounts has no fingerprint");
        Assert.AreEqual("fp-events", map["calendar-mcp/get_events"]);
        Assert.AreEqual("fp-search", map["ms365/search_emails"]);
    }

    [TestMethod]
    public void Capture_NothingResolvable_OrNotJson_IsNull()
    {
        Assert.IsNull(WispToolFingerprints.Capture(Definition, new StubDirectory([])));
        Assert.IsNull(WispToolFingerprints.Capture("print('hi')", new StubDirectory([])));
        Assert.IsNull(WispToolFingerprints.Capture(Definition, null));
    }

    [TestMethod]
    [DataRow("calendar-mcp/get_events", "calendar-mcp", "get_events")]
    [DataRow("fs/dir/list", "fs", "dir/list")]
    public void Keys_RoundTrip(string key, string server, string tool)
    {
        Assert.AreEqual(key, WispToolFingerprints.Key(server, tool));
        Assert.IsTrue(WispToolFingerprints.TryParseKey(key, out var s, out var t));
        Assert.AreEqual(server, s);
        Assert.AreEqual(tool, t);
    }

    private sealed class StubDirectory(Dictionary<(string?, string), McpToolEntry> entries) : IMcpToolDirectory
    {
        public IToolExecutor WrapperExecutor => throw new NotSupportedException();
        public McpToolEntry? Resolve(string? serverName, string tool) => entries.GetValueOrDefault((serverName, tool));
        public IReadOnlyList<McpToolEntry> ForServer(string serverName) => [];
        public bool IsServerIndexed(string serverName) => false;
    }
}

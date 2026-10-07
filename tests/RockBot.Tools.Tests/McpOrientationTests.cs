using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

/// <summary>
/// The MCP orientation in every run's context (#614, porting mcp-aggregator#48) stays short,
/// fits its mode, and never restates what the tool schemas already carry; the <c>mcp</c> guide
/// behind <c>get_tool_guide</c> matches the shipped surface.
/// </summary>
[TestClass]
public class McpOrientationTests
{
    private static readonly TypedToolMode[] AllModes = Enum.GetValues<TypedToolMode>();

    [TestMethod]
    public void Orientation_EveryMode_StaysUnderTheCeiling()
    {
        foreach (var mode in AllModes)
        {
            var text = McpOrientation.Build(mode);
            Assert.IsTrue(text.Length > 0, $"{mode}: empty");
            Assert.IsTrue(text.Length <= McpOrientation.MaxLength,
                $"{mode}: {text.Length} characters, over the {McpOrientation.MaxLength} ceiling");
        }
    }

    [TestMethod]
    public void Orientation_EveryMode_StartsWithTheHeadingThatMarksIt()
    {
        foreach (var mode in AllModes)
            Assert.IsTrue(McpOrientation.Build(mode).StartsWith(TypedToolSurfaceContext.OrientationHeading), $"{mode}");
    }

    [TestMethod]
    public void Orientation_EveryMode_PointsToTheServerSkillAndTheFullGuide()
    {
        foreach (var mode in AllModes)
        {
            var text = McpOrientation.Build(mode);
            StringAssert.Contains(text, "`mcp/{server}`", $"{mode}");
            StringAssert.Contains(text, "get_skill", $"{mode}");
            StringAssert.Contains(text, "get_tool_guide", $"{mode}");
            StringAssert.Contains(text, "mcp_invoke_tool", $"{mode}: the escape hatch is always there");
        }
    }

    [TestMethod]
    public void Orientation_NamesTheLoaderOnlyInModesThatHaveIt()
    {
        foreach (var mode in AllModes)
        {
            var text = McpOrientation.Build(mode);
            var hasLoader = mode is TypedToolMode.Lazy or TypedToolMode.Pinned;
            Assert.AreEqual(hasLoader, text.Contains(McpTypedToolSurface.FindToolsName),
                $"{mode}: mcp_find_tools is only in Lazy and Pinned runs' tool lists");
        }
    }

    [TestMethod]
    public void Orientation_TypedNamingConvention_OnlyWhenTypedToolsAreOn()
    {
        Assert.IsFalse(McpOrientation.Build(TypedToolMode.Off).Contains("{server}__{tool}"),
            "Off runs have no typed tools; naming them would send the model after tools it can't call");
        foreach (var mode in AllModes.Where(m => m != TypedToolMode.Off))
            StringAssert.Contains(McpOrientation.Build(mode), "{server}__{tool}", $"{mode}");
    }

    [TestMethod]
    public void Orientation_IsFixedPerMode()
    {
        foreach (var mode in AllModes)
            Assert.AreEqual(McpOrientation.Build(mode), McpOrientation.Build(mode), $"{mode}");
    }

    [TestMethod]
    public void Orientation_EveryMode_DoesNotRestateAnyToolDescription()
    {
        var descriptions = RegisteredDescriptions();
        foreach (var mode in AllModes)
        {
            var orientation = Normalize(McpOrientation.Build(mode));
            foreach (var (tool, description) in descriptions)
            {
                foreach (var window in Windows(Normalize(description), 8))
                {
                    Assert.IsFalse(orientation.Contains(window),
                        $"{mode}: the orientation restates {tool}'s description (\"{window}\")");
                }
            }
        }
    }

    [TestMethod]
    public void Surface_Orientation_IsTheBuiltTextForTheMode()
    {
        var surface = new McpTypedToolSurface(
            Microsoft.Extensions.Options.Options.Create(new McpToolSurfaceOptions()),
            NullLogger<McpTypedToolSurface>.Instance);

        foreach (var mode in AllModes)
            Assert.AreEqual(McpOrientation.Build(mode), ((ITypedToolSurface)surface).Orientation(mode), $"{mode}");
    }

    // ── Breadcrumbs ──────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("mcp_list_services")]
    [DataRow(McpTypedToolSurface.FindToolsName)]
    public void DiscoveryTools_PointToTheServerSkill(string tool)
    {
        // A model that goes straight to discovery never reads the orientation's pointer
        // otherwise (aggregator#48's list_services breadcrumb).
        var description = RegisteredDescriptions().Single(d => d.Tool == tool).Description;
        StringAssert.Contains(description, "mcp/{server}");
        StringAssert.Contains(description, "get_skill");
    }

    [TestMethod]
    public void RegisterServer_AdvertisesOnlyWhatItHonors()
    {
        var registry = RegisteredRegistry();
        var schema = registry.GetTools().Single(t => t.Name == "mcp_register_server").ParametersSchema!;
        using var doc = System.Text.Json.JsonDocument.Parse(schema);
        CollectionAssert.AreEquivalent(new[] { "name", "type", "url" },
            doc.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ── The mcp guide ────────────────────────────────────────────────────────

    [TestMethod]
    public void Guide_NamesEveryMcpMetaTool()
    {
        var guide = new McpToolSkillProvider().GetDocument();
        foreach (var (tool, _) in RegisteredDescriptions())
            StringAssert.Contains(guide, tool, $"the mcp guide doesn't mention {tool}");
    }

    [TestMethod]
    public void Guide_DoesNotCarryTheRetiredSurface()
    {
        var guide = new McpToolSkillProvider().GetDocument();
        Assert.IsFalse(Regex.IsMatch(guide, @"\b[Ff]ive\b"), "the guide still counts five management tools");
        Assert.IsFalse(guide.Contains("display_name"), "mcp_register_server has no display_name");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Every MCP meta-tool's model-facing description, as the registry carries it.</summary>
    private static List<(string Tool, string Description)> RegisteredDescriptions()
    {
        var descriptions = RegisteredRegistry().GetTools()
            .Where(t => t.Name.StartsWith("mcp_", StringComparison.Ordinal))
            .Select(t => (t.Name, t.Description))
            .ToList();
        descriptions.Add((McpTypedToolSurface.FindToolsName, McpFindToolsExecutor.Description));
        Assert.IsTrue(descriptions.Count >= 7, $"expected the six management tools plus mcp_find_tools, got {descriptions.Count}");
        return descriptions;
    }

    /// <summary>A registry holding the management tools, registered the way the agent does.</summary>
    private static ToolRegistry RegisteredRegistry()
    {
        var registry = new ToolRegistry();
        var index = new McpServerIndex();
        var identity = new AgentIdentity("test-agent");
        var proxy = new McpToolProxy(new TrackingPublisher(), new StubSubscriber(), identity,
            NullLogger<McpToolProxy>.Instance);
        var executor = new McpManagementExecutor(index, proxy, new TrackingPublisher(), new StubSubscriber(),
            identity, NullLogger<McpManagementExecutor>.Instance);
        var handler = new McpServersIndexedHandler(registry, index, executor,
            NullLogger<McpServersIndexedHandler>.Instance);

        var message = new McpServersIndexed
        {
            Servers = [new McpServerSummary { ServerName = "test", ToolCount = 0, ToolNames = [] }]
        };
        handler.HandleAsync(message, new MessageHandlerContext
        {
            Envelope = message.ToEnvelope("test-bridge"),
            Agent = identity,
            Services = null!,
            CancellationToken = CancellationToken.None
        }).GetAwaiter().GetResult();

        return registry;
    }

    private static string Normalize(string text) =>
        Regex.Replace(Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9_{}/ ]+", " "), @"\s+", " ").Trim();

    private static IEnumerable<string> Windows(string text, int size)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + size <= words.Length; i++)
            yield return string.Join(' ', words, i, size);
    }
}

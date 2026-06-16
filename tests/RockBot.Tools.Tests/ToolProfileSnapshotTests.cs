using RockBot.Tools;

namespace RockBot.Tools.Tests;

/// <summary>
/// Snapshots the tool surface each named profile admits over a canonical registry that
/// covers every known tool <c>Source</c> plus the specific tool names the profiles key on.
/// Acts as a drift detector: landing a new tool source (a new <c>ToolRegistrar</c>) means
/// adding it to <see cref="Canonical"/> here, which shifts the snapshots and forces a
/// conscious decision about whether each restricted profile should admit it.
/// </summary>
[TestClass]
public class ToolProfileSnapshotTests
{
    // One representative registration per known Source, plus the individually-named tools
    // that profiles allow/deny by name. Keep this list in sync with the live ToolRegistrars.
    private static readonly ToolRegistration[] Canonical =
    [
        // a2a (RockBot.A2A / A2ACallerToolRegistrar)
        R("invoke_agent", "a2a"),
        R("register_agent", "a2a"),
        R("unregister_agent", "a2a"),
        R("list_known_agents", "a2a"),
        R("get_agent_details", "a2a"),
        // mcp:management (McpServersIndexedHandler)
        R("mcp_invoke_tool", "mcp:management"),
        R("mcp_list_services", "mcp:management"),
        R("mcp_get_service_details", "mcp:management"),
        R("mcp_register_server", "mcp:management"),
        R("mcp_unregister_server", "mcp:management"),
        // mcp:{server} (McpToolRegistrar — per-server source)
        R("calendar__list_events", "mcp:calendar"),
        // scheduling (SchedulingToolRegistrar)
        R("create_scheduled_task", "scheduling"),
        R("cancel_scheduled_task", "scheduling"),
        // subagent (SubagentToolRegistrar)
        R("spawn_subagent", "subagent"),
        R("cancel_subagent", "subagent"),
        R("list_subagents", "subagent"),
        // web (WebToolRegistrar)
        R("web_search", "web"),
        R("browse_url", "web"),
        // script (ScriptToolRegistrar)
        R("run_script", "script"),
        // service-search (ServiceSearchToolRegistrar)
        R("service_search", "service-search"),
        // filesystem (FileSystemToolRegistrar)
        R("read_file", "filesystem"),
        // worker (WorkerToolRegistrar)
        R("spawn_worker", "worker"),
        // wisp (WispToolRegistrar)
        R("wisp_execute", "wisp"),
    ];

    private static ToolRegistration R(string name, string source) =>
        new() { Name = name, Description = name, Source = source };

    private static string[] Allowed(ToolProfile profile) =>
        Canonical.Where(profile.Matches).Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [TestMethod]
    public void Main_AdmitsEverything()
    {
        Assert.AreEqual(Canonical.Length, Allowed(ToolProfiles.Main).Length);
    }

    [TestMethod]
    public void Subagent_Snapshot()
    {
        // Denies sources subagent/scheduling/a2a and the two MCP-bridge reconfig tools.
        string[] expected =
        [
            "browse_url",
            "calendar__list_events",
            "mcp_get_service_details",
            "mcp_invoke_tool",
            "mcp_list_services",
            "read_file",
            "run_script",
            "service_search",
            "spawn_worker",
            "web_search",
            "wisp_execute",
        ];
        CollectionAssert.AreEqual(expected, Allowed(ToolProfiles.Subagent));
    }

    [TestMethod]
    public void Scheduled_Snapshot()
    {
        // Denies only the two MCP-bridge reconfig tools; subagent spawning stays available.
        var expected = Canonical
            .Select(r => r.Name)
            .Where(n => n is not ("mcp_register_server" or "mcp_unregister_server"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(expected, Allowed(ToolProfiles.Scheduled));
        Assert.IsTrue(Allowed(ToolProfiles.Scheduled).Contains("spawn_subagent"));
    }

    [TestMethod]
    public void A2ASynthesis_Snapshot()
    {
        // Denies the five A2A caller tools so synthesis cannot dispatch a fresh interaction.
        var allowed = Allowed(ToolProfiles.A2ASynthesis);
        foreach (var blocked in new[]
                 { "invoke_agent", "register_agent", "unregister_agent", "list_known_agents", "get_agent_details" })
            Assert.IsFalse(allowed.Contains(blocked), $"{blocked} should be denied");
        Assert.AreEqual(Canonical.Length - 5, allowed.Length);
    }
}

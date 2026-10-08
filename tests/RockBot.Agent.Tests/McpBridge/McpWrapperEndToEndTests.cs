using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// Typed <c>{server}__{tool}</c> wrappers end to end (#420): the agent-side catalog builds them
/// from the bridge's index, and a wrapper call travels the same path as <c>mcp_invoke_tool</c> —
/// proxy, bridge, real MCP server — and gets the same answer. Typed prompt tools (#616) likewise
/// get the same messages as <c>mcp_get_prompt</c>.
/// </summary>
[TestClass]
public class McpWrapperEndToEndTests
{
    internal sealed class Counter
    {
        private int _value;
        public int Value => Volatile.Read(ref _value);
        public void Increment() => Interlocked.Increment(ref _value);
    }

    internal static McpServerTool SendEmail(Counter executions) => McpServerTool.Create(
        (string[] to, string subject) =>
        {
            executions.Increment();
            return $"sent '{subject}' to {string.Join(",", to)}";
        },
        new McpServerToolCreateOptions { Name = "send_email", Description = "Sends an email." });

    internal static McpServerPrompt DailyBriefing(Counter fetches) => McpServerPrompt.Create(
        (string date, string? accountId = null) =>
        {
            fetches.Increment();
            return $"Brief me on {date} for {accountId ?? "all accounts"}";
        },
        new McpServerPromptCreateOptions { Name = "daily_briefing", Description = "A daily briefing." });

    internal static McpServerTool DeleteEverything() => McpServerTool.Create(
        () => "deleted",
        new McpServerToolCreateOptions { Name = "delete_everything" });

    internal sealed class AgentSide
    {
        public required TestToolRegistry Registry { get; init; }
        public required McpManagementExecutor Management { get; init; }
        public required McpWrapperCatalog Catalog { get; init; }
        public required McpTypedToolSurface Surface { get; init; }
        public required Func<Task> ReplayIndexAsync { get; init; }
    }

    /// <summary>The agent's half of the gateway, on the harness's in-memory bus, fed the bridge's index.</summary>
    internal static async Task<AgentSide> ConnectAgentAsync(
        BridgeHarness harness,
        McpWrapperMode mode = McpWrapperMode.Eager,
        Action<McpToolSurfaceOptions>? configure = null)
    {
        var identity = new AgentIdentity("test-agent");
        var proxy = new McpToolProxy(harness.BusPublisher, harness.BusSubscriber, identity,
            NullLogger<McpToolProxy>.Instance, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        var index = new McpServerIndex();
        var surfaceOptions = new McpToolSurfaceOptions { WrapperMode = mode };
        configure?.Invoke(surfaceOptions);
        var options = Options.Create(surfaceOptions);
        var surface = new McpTypedToolSurface(options, NullLogger<McpTypedToolSurface>.Instance);
        var management = new McpManagementExecutor(index, proxy, harness.BusPublisher, harness.BusSubscriber, identity,
            NullLogger<McpManagementExecutor>.Instance, TimeSpan.FromSeconds(10), typedTools: surface);
        var cache = ToolSchemaCache.WithPrompts((server, ct) => management.GetSurfaceAsync(server, ct));
        var registry = new TestToolRegistry();
        var catalog = new McpWrapperCatalog(registry, cache, management, options,
            NullLogger<McpWrapperCatalog>.Instance, surface);
        var handler = new McpServersIndexedHandler(registry, index, management,
            NullLogger<McpServersIndexedHandler>.Instance, cache, catalog);

        var context = new MessageHandlerContext
        {
            Envelope = new McpServersIndexed { Servers = [] }.ToEnvelope("bridge"),
            Agent = identity,
            Services = new ServiceCollection().BuildServiceProvider(),
            CancellationToken = CancellationToken.None
        };

        // Delivers every index message the bridge has published so far, as the bus would.
        var delivered = 0;
        async Task ReplayIndexAsync()
        {
            var messages = harness.IndexMessages;
            for (; delivered < messages.Count; delivered++)
                await handler.HandleAsync(messages[delivered], context);
        }

        await ReplayIndexAsync();
        return new AgentSide
        {
            Registry = registry, Management = management, Catalog = catalog, Surface = surface, ReplayIndexAsync = ReplayIndexAsync
        };
    }

    private static Task<ToolInvokeResponse> CallAsync(AgentSide agent, string tool, string arguments) =>
        agent.Registry.GetExecutor(tool)!.ExecuteAsync(
            new ToolInvokeRequest { ToolCallId = Guid.NewGuid().ToString("N"), ToolName = tool, Arguments = arguments },
            CancellationToken.None);

    [TestMethod]
    public async Task Index_RegistersATypedToolPerAllowedDownstreamTool()
    {
        await using var harness = await BridgeHarness.StartAsync(
            [SendEmail(new Counter()), DeleteEverything()], configure: c => c.DeniedTools = ["delete_everything"]);
        var agent = await ConnectAgentAsync(harness);

        var wrapper = agent.Registry.GetTools().Single(t => t.Name == "fixture__send_email");
        Assert.AreEqual("send_email", wrapper.DownstreamName);
        Assert.AreEqual("mcp:fixture", wrapper.Source);
        using var schema = JsonDocument.Parse(wrapper.ParametersSchema!);
        CollectionAssert.AreEquivalent(new[] { "to", "subject" },
            schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());

        Assert.IsNull(agent.Registry.GetExecutor("fixture__delete_everything"),
            "An operator-denied tool must not get a typed tool.");
        Assert.IsNotNull(agent.Registry.GetExecutor("mcp_invoke_tool"), "mcp_invoke_tool stays as the escape hatch.");
    }

    [TestMethod]
    public async Task TypedCall_AndInvokeTool_ReachTheSameToolWithTheSameResult()
    {
        var executions = new Counter();
        await using var harness = await BridgeHarness.StartAsync([SendEmail(executions)]);
        var agent = await ConnectAgentAsync(harness);

        var typed = await CallAsync(agent, "fixture__send_email", """{"to":["a@b.c"],"subject":"hi"}""");
        var generic = await CallAsync(agent, "mcp_invoke_tool",
            """{"server_name":"fixture","tool_name":"send_email","arguments":{"to":["a@b.c"],"subject":"hi"}}""");

        Assert.IsFalse(typed.IsError, typed.Content);
        Assert.AreEqual(generic.Content, typed.Content);
        Assert.AreEqual("sent 'hi' to a@b.c", typed.Content);
        Assert.AreEqual(2, executions.Value);
    }

    [TestMethod]
    public async Task TypedCall_MissingARequiredKey_NeverReachesTheServer()
    {
        var executions = new Counter();
        await using var harness = await BridgeHarness.StartAsync([SendEmail(executions)]);
        var agent = await ConnectAgentAsync(harness);

        var response = await CallAsync(agent, "fixture__send_email", """{"to":["a@b.c"]}""");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content, "[subject]");
        Assert.AreEqual(0, executions.Value);
    }

    [TestMethod]
    public async Task TypedCall_WithAWrongType_GetsTheBridgesTypeHint()
    {
        await using var harness = await BridgeHarness.StartAsync([SendEmail(new Counter())]);
        var agent = await ConnectAgentAsync(harness);

        var response = await CallAsync(agent, "fixture__send_email", """{"to":"a@b.c","subject":"hi"}""");

        Assert.IsTrue(response.IsError, response.Content);
        StringAssert.Contains(response.Content, "Parameter 'to' is declared as array but you sent a string.");
    }

    [TestMethod]
    public async Task ServerAddingATool_GetsATypedToolAfterTheRefresh()
    {
        await using var harness = await BridgeHarness.StartAsync([SendEmail(new Counter())]);
        var agent = await ConnectAgentAsync(harness);
        Assert.IsNull(agent.Registry.GetExecutor("fixture__delete_everything"));

        var sendBefore = agent.Registry.GetTools().Single(t => t.Name == "fixture__send_email");

        harness.AddServerTool(DeleteEverything());
        await harness.Bridge.RefreshSurfaceAsync(BridgeHarness.ServerName, CancellationToken.None);
        await agent.ReplayIndexAsync();

        Assert.IsNotNull(agent.Registry.GetExecutor("fixture__delete_everything"));
        Assert.AreSame(sendBefore, agent.Registry.GetTools().Single(t => t.Name == "fixture__send_email"),
            "An unchanged tool keeps its registration when its server's surface changes.");
    }

    // ── Typed prompt tools (#616) ─────────────────────────────────────────────

    private const string BriefingTool = "fixture__daily_briefing-prompt";

    private static async Task<string?> CallFunctionAsync(Microsoft.Extensions.AI.AIFunction function, Dictionary<string, object?> arguments) =>
        (await function.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(arguments)))?.ToString();

    [TestMethod]
    public async Task TypedPrompt_AndGetPrompt_ReturnTheSameMessages()
    {
        var fetches = new Counter();
        await using var harness = await BridgeHarness.StartAsync([SendEmail(new Counter())], [DailyBriefing(fetches)]);
        var agent = await ConnectAgentAsync(harness, McpWrapperMode.Lazy);

        var function = agent.Surface.ActivateByName("session-1", BriefingTool);
        Assert.IsNotNull(function, "The bridge's prompt has a typed tool in lazy mode.");
        var typed = await CallFunctionAsync(function, new() { ["date"] = "2026-10-08" });
        var generic = await CallAsync(agent, "mcp_get_prompt",
            """{"server_name":"fixture","prompt_name":"daily_briefing","arguments":{"date":"2026-10-08"}}""");

        Assert.IsFalse(generic.IsError, generic.Content);
        Assert.AreEqual(generic.Content, typed);
        StringAssert.Contains(typed, "Brief me on 2026-10-08 for all accounts");
        Assert.AreEqual(2, fetches.Value);
    }

    [TestMethod]
    public async Task TypedPrompt_MissingARequiredArgument_NeverReachesTheServer()
    {
        var fetches = new Counter();
        await using var harness = await BridgeHarness.StartAsync([SendEmail(new Counter())], [DailyBriefing(fetches)]);
        var agent = await ConnectAgentAsync(harness, McpWrapperMode.Pinned);

        var function = agent.Surface.ActivateByName("session-1", BriefingTool)!;
        var result = await CallFunctionAsync(function, new() { ["accountId"] = "work" });

        StringAssert.StartsWith(result, "Error:");
        StringAssert.Contains(result, "[date]");
        Assert.AreEqual(0, fetches.Value);
    }

    [TestMethod]
    public async Task EagerMode_HasNoTypedPrompt_ButGetPromptStillWorks()
    {
        await using var harness = await BridgeHarness.StartAsync([SendEmail(new Counter())], [DailyBriefing(new Counter())]);
        var agent = await ConnectAgentAsync(harness);

        Assert.AreEqual(0, agent.Catalog.PromptWrappers.Count);
        var generic = await CallAsync(agent, "mcp_get_prompt",
            """{"server_name":"fixture","prompt_name":"daily_briefing","arguments":{"date":"2026-10-08"}}""");
        Assert.IsFalse(generic.IsError, generic.Content);
        StringAssert.Contains(generic.Content, "Brief me on 2026-10-08");
    }

    /// <summary>Minimal <see cref="IToolRegistry"/> (the production one is internal to RockBot.Tools).</summary>
    internal sealed class TestToolRegistry : IToolRegistry
    {
        private readonly ConcurrentDictionary<string, (ToolRegistration Registration, IToolExecutor Executor)> _tools = new();

        public IReadOnlyList<ToolRegistration> GetTools() => [.. _tools.Values.Select(t => t.Registration)];

        public IToolExecutor? GetExecutor(string toolName) =>
            _tools.TryGetValue(toolName, out var entry) ? entry.Executor : null;

        public void Register(ToolRegistration registration, IToolExecutor executor)
        {
            if (!_tools.TryAdd(registration.Name, (registration, executor)))
                throw new InvalidOperationException($"Tool '{registration.Name}' is already registered.");
        }

        public bool Unregister(string toolName) => _tools.TryRemove(toolName, out _);
    }
}

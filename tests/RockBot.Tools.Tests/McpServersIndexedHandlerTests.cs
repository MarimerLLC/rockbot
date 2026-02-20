using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

[TestClass]
public class McpServersIndexedHandlerTests
{
    private readonly ToolRegistry _registry = new();
    private readonly McpServerIndex _index = new();
    private readonly AgentIdentity _identity = new("test-agent");

    private McpServersIndexedHandler CreateHandler()
    {
        var proxy = new McpToolProxy(
            new TrackingPublisher(),
            new StubSubscriber(),
            _identity,
            NullLogger<McpToolProxy>.Instance);

        var executor = new McpManagementExecutor(
            _index,
            proxy,
            new TrackingPublisher(),
            new StubSubscriber(),
            _identity,
            NullLogger<McpManagementExecutor>.Instance);

        return new McpServersIndexedHandler(
            _registry,
            _index,
            executor,
            NullLogger<McpServersIndexedHandler>.Instance);
    }

    [TestInitialize]
    public void ResetIndex()
    {
        // Reset the singleton flag between tests
        _index.ManagementToolsRegistered = false;
    }

    [TestMethod]
    public async Task HandleAsync_PopulatesServerIndex()
    {
        var handler = CreateHandler();
        var message = new McpServersIndexed
        {
            Servers =
            [
                new McpServerSummary
                {
                    ServerName = "filesystem",
                    Summary = "File system tools.",
                    ToolCount = 2,
                    ToolNames = ["read_file", "write_file"]
                }
            ]
        };

        await handler.HandleAsync(message, CreateContext(message));

        Assert.AreEqual(1, _index.Servers.Count);
        Assert.AreEqual("filesystem", _index.Servers[0].ServerName);
        Assert.AreEqual(2, _index.Servers[0].ToolCount);
    }

    [TestMethod]
    public async Task HandleAsync_FirstMessage_RegistersExactlyFiveManagementTools()
    {
        var handler = CreateHandler();
        var message = new McpServersIndexed
        {
            Servers = [new McpServerSummary { ServerName = "test", ToolCount = 0, ToolNames = [] }]
        };

        await handler.HandleAsync(message, CreateContext(message));

        var tools = _registry.GetTools();
        Assert.AreEqual(5, tools.Count);

        var names = tools.Select(t => t.Name).ToHashSet();
        Assert.IsTrue(names.Contains("mcp_list_services"));
        Assert.IsTrue(names.Contains("mcp_get_service_details"));
        Assert.IsTrue(names.Contains("mcp_invoke_tool"));
        Assert.IsTrue(names.Contains("mcp_register_server"));
        Assert.IsTrue(names.Contains("mcp_unregister_server"));
    }

    [TestMethod]
    public async Task HandleAsync_SubsequentMessages_DoNotReRegisterTools()
    {
        var handler = CreateHandler();
        var message = new McpServersIndexed
        {
            Servers = [new McpServerSummary { ServerName = "a", ToolCount = 0, ToolNames = [] }]
        };

        await handler.HandleAsync(message, CreateContext(message));
        await handler.HandleAsync(message, CreateContext(message));

        // Tools should be registered exactly once — still 5, not 10
        Assert.AreEqual(5, _registry.GetTools().Count);
    }

    [TestMethod]
    public async Task HandleAsync_RemovedServers_RemovesFromIndex()
    {
        var handler = CreateHandler();

        // First message: add two servers
        var add = new McpServersIndexed
        {
            Servers =
            [
                new McpServerSummary { ServerName = "a", ToolCount = 1, ToolNames = ["tool_a"] },
                new McpServerSummary { ServerName = "b", ToolCount = 1, ToolNames = ["tool_b"] }
            ]
        };
        await handler.HandleAsync(add, CreateContext(add));

        Assert.AreEqual(2, _index.Servers.Count);

        // Second message: remove server "a"
        var remove = new McpServersIndexed
        {
            Servers = [],
            RemovedServers = ["a"]
        };
        await handler.HandleAsync(remove, CreateContext(remove));

        Assert.AreEqual(1, _index.Servers.Count);
        Assert.AreEqual("b", _index.Servers[0].ServerName);
    }

    [TestMethod]
    public async Task HandleAsync_UpdatedServer_ReplacesExistingEntry()
    {
        var handler = CreateHandler();

        var v1 = new McpServersIndexed
        {
            Servers = [new McpServerSummary { ServerName = "fs", ToolCount = 1, ToolNames = ["read_file"] }]
        };
        await handler.HandleAsync(v1, CreateContext(v1));

        var v2 = new McpServersIndexed
        {
            Servers = [new McpServerSummary { ServerName = "fs", ToolCount = 2, ToolNames = ["read_file", "write_file"] }]
        };
        await handler.HandleAsync(v2, CreateContext(v2));

        Assert.AreEqual(1, _index.Servers.Count);
        Assert.AreEqual(2, _index.Servers[0].ToolCount);
    }

    [TestMethod]
    public async Task HandleAsync_ManagementToolsSourceTaggedCorrectly()
    {
        var handler = CreateHandler();
        var message = new McpServersIndexed
        {
            Servers = [new McpServerSummary { ServerName = "x", ToolCount = 0, ToolNames = [] }]
        };

        await handler.HandleAsync(message, CreateContext(message));

        foreach (var tool in _registry.GetTools())
        {
            Assert.AreEqual("mcp:management", tool.Source,
                $"Tool {tool.Name} should have source 'mcp:management'");
        }
    }

    private static MessageHandlerContext CreateContext<T>(T payload)
    {
        var envelope = payload.ToEnvelope("test-bridge");
        return new MessageHandlerContext
        {
            Envelope = envelope,
            Agent = new AgentIdentity("test-agent"),
            Services = null!,
            CancellationToken = CancellationToken.None
        };
    }
}

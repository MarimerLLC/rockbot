using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

[TestClass]
public class McpToolsAvailableHandlerTests
{
    private readonly ToolRegistry _registry = new();
    private readonly McpToolProxy _proxy;
    private readonly McpToolsAvailableHandler _handler;

    public McpToolsAvailableHandlerTests()
    {
        _proxy = new McpToolProxy(
            new TrackingPublisher(),
            new StubSubscriber(),
            new AgentIdentity("test-agent"),
            NullLogger<McpToolProxy>.Instance);

        _handler = new McpToolsAvailableHandler(
            _registry,
            _proxy,
            NullLogger<McpToolsAvailableHandler>.Instance);
    }

    [TestMethod]
    public async Task HandleAsync_RegistersNewTools()
    {
        var message = new McpToolsAvailable
        {
            ServerName = "test-server",
            Tools =
            [
                new McpToolDefinition
                {
                    Name = "read_file",
                    Description = "Reads a file",
                    ParametersSchema = """{"type":"object"}"""
                }
            ],
            RemovedTools = []
        };

        var context = CreateContext(message);
        await _handler.HandleAsync(message, context);

        var tools = _registry.GetTools();
        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual("read_file", tools[0].Name);
        Assert.AreEqual("mcp:test-server", tools[0].Source);
    }

    [TestMethod]
    public async Task HandleAsync_RemovesTools()
    {
        // Pre-register a tool
        _registry.Register(new ToolRegistration
        {
            Name = "old_tool",
            Description = "Old tool",
            Source = "mcp:test-server"
        }, _proxy);

        var message = new McpToolsAvailable
        {
            ServerName = "test-server",
            Tools = [],
            RemovedTools = ["old_tool"]
        };

        var context = CreateContext(message);
        await _handler.HandleAsync(message, context);

        Assert.AreEqual(0, _registry.GetTools().Count);
        Assert.IsNull(_registry.GetExecutor("old_tool"));
    }

    [TestMethod]
    public async Task HandleAsync_UpdatesExistingTool()
    {
        // Pre-register a tool
        _registry.Register(new ToolRegistration
        {
            Name = "tool_a",
            Description = "Old description",
            Source = "mcp:test-server"
        }, _proxy);

        var message = new McpToolsAvailable
        {
            ServerName = "test-server",
            Tools =
            [
                new McpToolDefinition
                {
                    Name = "tool_a",
                    Description = "New description",
                    ParametersSchema = """{"type":"object"}"""
                }
            ],
            RemovedTools = []
        };

        var context = CreateContext(message);
        await _handler.HandleAsync(message, context);

        var tools = _registry.GetTools();
        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual("New description", tools[0].Description);
    }

    [TestMethod]
    public async Task HandleAsync_RegistersMultipleTools()
    {
        var message = new McpToolsAvailable
        {
            ServerName = "multi-server",
            Tools =
            [
                new McpToolDefinition { Name = "tool_a", Description = "A" },
                new McpToolDefinition { Name = "tool_b", Description = "B" },
                new McpToolDefinition { Name = "tool_c", Description = "C" }
            ],
            RemovedTools = []
        };

        var context = CreateContext(message);
        await _handler.HandleAsync(message, context);

        Assert.AreEqual(3, _registry.GetTools().Count);
    }

    [TestMethod]
    public async Task HandleAsync_UsesProxyAsExecutor()
    {
        var message = new McpToolsAvailable
        {
            ServerName = "test-server",
            Tools =
            [
                new McpToolDefinition { Name = "my_tool", Description = "Test" }
            ],
            RemovedTools = []
        };

        var context = CreateContext(message);
        await _handler.HandleAsync(message, context);

        var executor = _registry.GetExecutor("my_tool");
        Assert.AreSame(_proxy, executor);
    }

    [TestMethod]
    public async Task HandleAsync_EmptyMessage_NoOp()
    {
        var message = new McpToolsAvailable
        {
            ServerName = "empty",
            Tools = [],
            RemovedTools = []
        };

        var context = CreateContext(message);
        await _handler.HandleAsync(message, context);

        Assert.AreEqual(0, _registry.GetTools().Count);
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

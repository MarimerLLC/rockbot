using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Handles <see cref="McpServersIndexed"/> messages from the MCP Bridge.
/// On the first message, registers the 5 MCP management tools in <see cref="IToolRegistry"/>.
/// All subsequent messages only update the <see cref="McpServerIndex"/> cache.
/// </summary>
public sealed class McpServersIndexedHandler(
    IToolRegistry registry,
    McpServerIndex index,
    McpManagementExecutor executor,
    ILogger<McpServersIndexedHandler> logger) : IMessageHandler<McpServersIndexed>
{
    public Task HandleAsync(McpServersIndexed message, MessageHandlerContext context)
    {
        index.Apply(message);

        logger.LogInformation(
            "MCP server index updated: {Added} added/updated, {Removed} removed",
            message.Servers.Count, message.RemovedServers.Count);

        if (!index.ManagementToolsRegistered)
        {
            RegisterManagementTools();
            index.ManagementToolsRegistered = true;
        }

        return Task.CompletedTask;
    }

    private void RegisterManagementTools()
    {
        registry.Register(new ToolRegistration
        {
            Name = "mcp_list_services",
            Description = "List all available MCP servers with their summaries and tool counts.",
            ParametersSchema = """{"type":"object","properties":{},"required":[]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_get_service_details",
            Description = "Get the full list of tools (names, descriptions, schemas) for a specific MCP server.",
            ParametersSchema = """{"type":"object","properties":{"server_name":{"type":"string","description":"Name of the MCP server"}},"required":["server_name"]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_invoke_tool",
            Description = "Invoke a specific tool on a specific MCP server.",
            ParametersSchema = """{"type":"object","properties":{"server_name":{"type":"string","description":"Name of the MCP server"},"tool_name":{"type":"string","description":"Name of the tool to invoke"},"arguments":{"type":"object","description":"Arguments to pass to the tool (as a JSON object)"}},"required":["server_name","tool_name"]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_register_server",
            Description = "Register a new MCP server at runtime via SSE transport.",
            ParametersSchema = """{"type":"object","properties":{"name":{"type":"string","description":"Unique server name"},"type":{"type":"string","enum":["sse"],"description":"Transport type"},"url":{"type":"string","description":"SSE endpoint URL"},"display_name":{"type":"string","description":"Human-readable display name"},"description":{"type":"string","description":"Server description"}},"required":["name","type","url"]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_unregister_server",
            Description = "Remove an MCP server at runtime.",
            ParametersSchema = """{"type":"object","properties":{"server_name":{"type":"string","description":"Name of the MCP server to remove"}},"required":["server_name"]}""",
            Source = "mcp:management"
        }, executor);

        logger.LogInformation("Registered 5 MCP management tools");
    }
}

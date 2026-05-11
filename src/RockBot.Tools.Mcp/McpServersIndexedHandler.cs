using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Handles <see cref="McpServersIndexed"/> messages from the MCP Bridge.
/// On the first message, registers the 6 MCP management tools in <see cref="IToolRegistry"/>.
/// All subsequent messages only update the <see cref="McpServerIndex"/> cache and
/// invalidate any cached tool schemas for the affected servers.
/// </summary>
public sealed class McpServersIndexedHandler(
    IToolRegistry registry,
    McpServerIndex index,
    McpManagementExecutor executor,
    ILogger<McpServersIndexedHandler> logger,
    ToolSchemaCache? schemaCache = null) : IMessageHandler<McpServersIndexed>
{
    public Task HandleAsync(McpServersIndexed message, MessageHandlerContext context)
    {
        index.Apply(message);

        // Invalidate cached schemas for any server whose summary changed or that
        // was removed. Schemas are server-state, so a reconnect invalidates the
        // cache for that server. Lookups re-fetch lazily.
        if (schemaCache is not null)
        {
            foreach (var server in message.Servers)
                schemaCache.Invalidate(server.ServerName);
            foreach (var removed in message.RemovedServers)
                schemaCache.Invalidate(removed);
        }

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
            Description = "List all connected MCP servers with their summaries and tool counts. Call this first when you need live, personal, or external data (calendar, email, files, etc.) and don't know which server to use.",
            ParametersSchema = """{"type":"object","properties":{},"required":[]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_get_service_details",
            Description = "Get tool details (name, description, parameter schema) for an MCP server. Pass tool_name to get details for one specific tool (preferred — avoids returning all tool schemas). Omit tool_name to list all tools on the server.",
            ParametersSchema = """{"type":"object","properties":{"server_name":{"type":"string","description":"Name of the MCP server"},"tool_name":{"type":"string","description":"Optional: return details for this specific tool only. Recommended when you know the tool name."}},"required":["server_name"]}""",
            Source = "mcp:management"
        }, executor);

        registry.Register(new ToolRegistration
        {
            Name = "mcp_invoke_tool",
            Description = "Execute a tool on an MCP server to access live external data or perform actions (e.g. read calendar events, send email, query files). This is the gateway to all external MCP capabilities. Requires server_name and tool_name from mcp_list_services/mcp_get_service_details.",
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

        registry.Register(new ToolRegistration
        {
            Name = "mcp_get_prompt",
            Description = "Invoke a prompt template on an MCP server. Returns filled-in messages (user/assistant) ready to use as context or instructions. Use mcp_get_service_details to see available prompt templates and their argument schemas first.",
            ParametersSchema = """{"type":"object","properties":{"server_name":{"type":"string","description":"Name of the MCP server"},"prompt_name":{"type":"string","description":"Name of the prompt template to invoke"},"arguments":{"type":"object","description":"Key-value arguments to fill in the prompt template (all values as strings)"}},"required":["server_name","prompt_name"]}""",
            Source = "mcp:management"
        }, executor);

        logger.LogInformation("Registered 6 MCP management tools");
    }
}

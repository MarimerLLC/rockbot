using RockBot.Host;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Handles <see cref="McpToolsAvailable"/> messages from the MCP Bridge.
/// Registers new tools and removes stale ones from the local <see cref="IToolRegistry"/>.
/// </summary>
public sealed class McpToolsAvailableHandler(
    IToolRegistry registry,
    McpToolProxy proxy,
    ILogger<McpToolsAvailableHandler> logger) : IMessageHandler<McpToolsAvailable>
{
    public Task HandleAsync(McpToolsAvailable message, MessageHandlerContext context)
    {
        // Remove tools that are no longer available
        foreach (var toolName in message.RemovedTools)
        {
            if (registry.Unregister(toolName))
            {
                logger.LogInformation("Unregistered MCP tool: {ToolName} from server {ServerName}",
                    toolName, message.ServerName);
            }
        }

        // Register new/updated tools
        foreach (var tool in message.Tools)
        {
            // Unregister first in case of update (tool schema changed, etc.)
            registry.Unregister(tool.Name);

            var registration = new ToolRegistration
            {
                Name = tool.Name,
                Description = tool.Description,
                ParametersSchema = tool.ParametersSchema,
                Source = $"mcp:{message.ServerName}"
            };

            registry.Register(registration, proxy);

            logger.LogInformation("Registered MCP tool: {ToolName} from server {ServerName}",
                tool.Name, message.ServerName);
        }

        return Task.CompletedTask;
    }
}

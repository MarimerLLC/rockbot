using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;

namespace RockBot.Tools.Mcp;

/// <summary>
/// DI registration extensions for MCP tool backends.
/// </summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Registers MCP tool servers for in-process execution (used by the MCP Bridge process).
    /// </summary>
    public static AgentHostBuilder AddMcpTools(
        this AgentHostBuilder builder,
        Action<McpOptions> configure)
    {
        var options = new McpOptions();
        configure(options);
        builder.Services.AddSingleton(options);

        builder.Services.AddHostedService<McpToolRegistrar>();

        return builder;
    }

    /// <summary>
    /// Registers the MCP tool proxy for agents that invoke MCP tools via the message bus.
    /// Subscribes to tool responses and tool discovery messages for this agent.
    /// </summary>
    public static AgentHostBuilder AddMcpToolProxy(this AgentHostBuilder builder)
    {
        var agentName = builder.Identity.Name;

        builder.Services.AddSingleton<McpToolProxy>();

        builder.HandleMessage<McpToolsAvailable, McpToolsAvailableHandler>();
        builder.SubscribeTo($"tool.meta.mcp.{agentName}");

        return builder;
    }
}

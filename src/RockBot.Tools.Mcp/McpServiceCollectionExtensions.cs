using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Host;
using RockBot.Tools;

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
    /// Registers the MCP management proxy for agents that interact with MCP servers via
    /// the message bus. On startup the bridge sends <see cref="McpServersIndexed"/>;
    /// the handler registers exactly 5 management tools in <see cref="IToolRegistry"/>
    /// instead of one tool per schema.
    /// </summary>
    public static AgentHostBuilder AddMcpToolProxy(this AgentHostBuilder builder)
    {
        var agentName = builder.Identity.Name;

        builder.Services.AddSingleton<McpToolProxy>();
        builder.Services.AddSingleton<McpServerIndex>();
        builder.Services.AddSingleton<McpManagementExecutor>();
        builder.Services.AddHostedService<McpStartupProbeService>();
        builder.Services.AddSingleton<IToolSkillProvider, McpToolSkillProvider>();

        builder.HandleMessage<McpServersIndexed, McpServersIndexedHandler>();
        builder.SubscribeTo($"tool.meta.mcp.{agentName}");

        // Note: mcp.manage.response.{agentName} is subscribed directly by
        // McpManagementExecutor (lazy, on first management call) — not via the pipeline.

        return builder;
    }
}

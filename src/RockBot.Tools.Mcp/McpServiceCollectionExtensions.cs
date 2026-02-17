using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;

namespace RockBot.Tools.Mcp;

/// <summary>
/// DI registration extensions for MCP tool backends.
/// </summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Registers MCP tool servers and their registrar.
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
}

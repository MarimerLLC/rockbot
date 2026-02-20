using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;

namespace RockBot.Tools;

/// <summary>
/// DI registration extensions for the tool handler.
/// </summary>
public static class ToolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the tool registry, tool invoke handler, and subscribes to "tool.invoke".
    /// </summary>
    public static AgentHostBuilder AddToolHandler(
        this AgentHostBuilder builder,
        Action<ToolOptions>? configure = null)
    {
        var options = new ToolOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        builder.Services.AddSingleton<ToolRegistry>();
        builder.Services.AddSingleton<IToolRegistry>(sp => sp.GetRequiredService<ToolRegistry>());

        // ToolGuideTools aggregates all IToolSkillProvider registrations so the agent
        // can discover and read usage docs for whichever tool services are in scope.
        builder.Services.AddSingleton<ToolGuideTools>();

        builder.HandleMessage<ToolInvokeRequest, ToolInvokeHandler>();
        builder.SubscribeTo("tool.invoke");

        return builder;
    }
}

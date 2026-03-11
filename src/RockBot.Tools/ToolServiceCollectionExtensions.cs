using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;

namespace RockBot.Tools;

/// <summary>
/// DI registration extensions for the tool registry.
/// </summary>
public static class ToolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the tool registry so executors can be discovered and tools
    /// can be exposed to the LLM via <see cref="RegistryToolFunction"/>.
    /// </summary>
    public static AgentHostBuilder AddToolHandler(this AgentHostBuilder builder)
    {
        builder.Services.AddSingleton<ToolRegistry>();
        builder.Services.AddSingleton<IToolRegistry>(sp => sp.GetRequiredService<ToolRegistry>());

        // ToolGuideTools aggregates all IToolSkillProvider registrations so the agent
        // can discover and read usage docs for whichever tool services are in scope.
        builder.Services.AddSingleton<ToolGuideTools>();

        return builder;
    }
}

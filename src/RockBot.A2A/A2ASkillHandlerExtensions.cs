using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RockBot.Host;

namespace RockBot.A2A;

/// <summary>
/// Extension methods for registering <see cref="IAgentSkillHandler"/> implementations
/// with the framework's skill-dispatch plumbing. An agent that registers any skill
/// handlers gets an automatically-wired <see cref="IAgentTaskHandler"/> that
/// dispatches by skill id, and its <see cref="AgentCard.Skills"/> is auto-populated
/// from the registered handlers' <see cref="IAgentSkillHandler.Skill"/> metadata.
/// </summary>
public static class A2ASkillHandlerExtensions
{
    /// <summary>
    /// Register a concrete <see cref="IAgentSkillHandler"/> implementation. The
    /// framework auto-wires dispatch and adds the handler's <see cref="AgentSkill"/>
    /// to the advertised card at startup.
    /// </summary>
    public static AgentHostBuilder AddSkillHandler<T>(this AgentHostBuilder builder)
        where T : class, IAgentSkillHandler
    {
        builder.Services.AddScoped<T>();
        builder.Services.AddScoped<IAgentSkillHandler>(sp => sp.GetRequiredService<T>());
        EnsureDispatcherRegistered(builder.Services);
        return builder;
    }

    /// <summary>
    /// Register multiple <see cref="IAgentSkillHandler"/> implementations by type.
    /// Each type must implement <see cref="IAgentSkillHandler"/> and be instantiable
    /// via DI.
    /// </summary>
    public static AgentHostBuilder AddSkillHandlers(
        this AgentHostBuilder builder, params Type[] skillHandlerTypes)
    {
        foreach (var type in skillHandlerTypes)
        {
            if (!typeof(IAgentSkillHandler).IsAssignableFrom(type))
            {
                throw new ArgumentException(
                    $"Type {type.FullName} does not implement {nameof(IAgentSkillHandler)}.",
                    nameof(skillHandlerTypes));
            }

            builder.Services.AddScoped(type);
            builder.Services.AddScoped(
                typeof(IAgentSkillHandler),
                sp => sp.GetRequiredService(type));
        }
        EnsureDispatcherRegistered(builder.Services);
        return builder;
    }

    private static void EnsureDispatcherRegistered(IServiceCollection services) =>
        services.TryAddScoped<IAgentTaskHandler, SkillDispatchingTaskHandler>();
}

using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Subagent;

/// <summary>
/// DI registration extensions for the subagent subsystem.
/// </summary>
public static class SubagentServiceCollectionExtensions
{
    /// <summary>
    /// Adds subagent spawning support, whiteboard memory, and associated tools.
    /// </summary>
    public static AgentHostBuilder AddSubagents(
        this AgentHostBuilder builder,
        Action<SubagentOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<SubagentOptions>(_ => { });

        // Core infrastructure
        builder.Services.AddSingleton<ISubagentManager, SubagentManager>();
        builder.Services.AddSingleton<IWhiteboardMemory, InMemoryWhiteboardMemory>();
        builder.Services.AddTransient<SubagentRunner>();

        // Message handlers for primary agent side
        builder.HandleMessage<SubagentProgressMessage, SubagentProgressHandler>();
        builder.HandleMessage<SubagentResultMessage, SubagentResultHandler>();
        builder.SubscribeTo(SubagentTopics.Progress);
        builder.SubscribeTo(SubagentTopics.Result);

        // Tool registrar (registers spawn_subagent, cancel_subagent, list_subagents)
        builder.Services.AddHostedService<SubagentToolRegistrar>();

        // Skill guide
        builder.Services.AddSingleton<IToolSkillProvider, SubagentToolSkillProvider>();

        return builder;
    }
}

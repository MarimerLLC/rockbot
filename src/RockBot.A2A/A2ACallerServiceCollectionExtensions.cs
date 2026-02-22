using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// DI registration extensions for the A2A caller side (primary agent invoking external agents).
/// </summary>
public static class A2ACallerServiceCollectionExtensions
{
    /// <summary>
    /// Registers A2A caller tools (<c>invoke_agent</c>, <c>list_known_agents</c>) and
    /// result/error/status handlers that fold external agent responses into the primary
    /// agent's LLM conversation. Reuses <see cref="A2AOptions"/> if already registered
    /// by <c>AddA2A()</c>, otherwise registers a fresh instance.
    /// </summary>
    public static AgentHostBuilder AddA2ACaller(
        this AgentHostBuilder builder,
        Action<A2AOptions>? configure = null)
    {
        // Register A2AOptions (may already be registered by AddA2A — TryAdd avoids double-register)
        var options = new A2AOptions();
        configure?.Invoke(options);
        builder.Services.TryAddSingleton(options);

        // Agent directory — shared with AddA2A if both are called
        builder.Services.TryAddSingleton<AgentDirectory>();
        builder.Services.TryAddSingleton<IAgentDirectory>(
            sp => sp.GetRequiredService<AgentDirectory>());

        // Discovery hosted service — shared with AddA2A if both are called.
        // Guard on the concrete type: IHostedService has many registrations, so
        // TryAddSingleton<IHostedService> would always be skipped.
        if (!builder.Services.Any(sd => sd.ServiceType == typeof(AgentDiscoveryService)))
        {
            builder.Services.AddSingleton<AgentDiscoveryService>();
            builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                sp => sp.GetRequiredService<AgentDiscoveryService>());
        }

        // Pending task tracker
        builder.Services.AddSingleton<A2ATaskTracker>();

        // Message handlers for result/error/status
        builder.HandleMessage<AgentTaskResult, A2ATaskResultHandler>();
        builder.HandleMessage<AgentTaskError, A2ATaskErrorHandler>();
        builder.HandleMessage<AgentTaskStatusUpdate, A2ATaskStatusHandler>();

        // Subscribe to the per-agent result topic (agent.response.{agentName})
        // and the shared status topic
        var agentName = builder.Identity.Name;
        var resultTopic = $"{options.CallerResultTopic}.{agentName}";
        builder.SubscribeTo(resultTopic);
        builder.SubscribeTo(options.StatusTopic);

        // Tool registration hosted service
        builder.Services.AddHostedService<A2ACallerToolRegistrar>();

        // Skill guide
        builder.Services.AddSingleton<IToolSkillProvider, A2ACallerSkillProvider>();

        return builder;
    }
}

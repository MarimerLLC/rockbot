using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RockBot.Scripts;
using RockBot.Tools;

namespace RockBot.Scripts.Remote;

/// <summary>
/// DI registration extensions for the message-bus-based remote script runner.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IScriptRunner"/> backed by the RabbitMQ message bus, routing
    /// requests to the Script Manager pod and awaiting results on the agent's reply topic.
    /// Also registers the <c>execute_python_script</c> tool in <see cref="IToolRegistry"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="agentName">
    /// The agent's unique name, used to construct the reply topic
    /// <c>script.result.{agentName}</c>.
    /// </param>
    public static IServiceCollection AddRemoteScriptRunner(
        this IServiceCollection services,
        string agentName)
    {
        // Core message-bus runner (singleton so pending-request dictionary is shared)
        services.AddSingleton<MessageBusScriptRunner>(sp =>
            new MessageBusScriptRunner(
                sp.GetRequiredService<RockBot.Messaging.IMessagePublisher>(),
                agentName));

        services.TryAddSingleton<IScriptRunner>(sp =>
            sp.GetRequiredService<MessageBusScriptRunner>());

        // Hosted service: subscribes to script.result.{agentName} and completes pending requests
        services.AddSingleton<ScriptResultSubscriber>();
        services.AddHostedService(sp => sp.GetRequiredService<ScriptResultSubscriber>());

        // Tool integration
        services.AddSingleton<ScriptToolExecutor>();
        services.AddHostedService<ScriptToolRegistrar>();
        services.AddSingleton<IToolSkillProvider, ScriptToolSkillProvider>();

        return services;
    }
}

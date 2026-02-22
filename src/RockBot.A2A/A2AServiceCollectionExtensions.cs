using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;

namespace RockBot.A2A;

/// <summary>
/// DI registration extensions for the A2A protocol layer.
/// </summary>
public static class A2AServiceCollectionExtensions
{
    /// <summary>
    /// Registers the A2A task handlers, discovery service, and agent directory.
    /// The user must separately register their <see cref="IAgentTaskHandler"/> implementation.
    /// </summary>
    public static AgentHostBuilder AddA2A(
        this AgentHostBuilder builder,
        Action<A2AOptions>? configure = null)
    {
        var options = new A2AOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        // Agent directory — persists to disk and loads on startup
        builder.Services.AddSingleton<AgentDirectory>();
        builder.Services.AddSingleton<IAgentDirectory>(sp => sp.GetRequiredService<AgentDirectory>());
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            sp => sp.GetRequiredService<AgentDirectory>());

        // Discovery hosted service
        builder.Services.AddSingleton<AgentDiscoveryService>();
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            sp => sp.GetRequiredService<AgentDiscoveryService>());

        // Task request handler on agent.task.{agentName}
        var agentName = builder.Identity.Name;

        builder.HandleMessage<AgentTaskRequest, AgentTaskRequestHandler>();
        builder.SubscribeTo($"{options.TaskTopic}.{agentName}");

        // Cancel handler on agent.task.cancel.{agentName}
        builder.HandleMessage<AgentTaskCancelRequest, AgentTaskCancelHandler>();
        builder.SubscribeTo($"{options.CancelTopic}.{agentName}");

        return builder;
    }
}

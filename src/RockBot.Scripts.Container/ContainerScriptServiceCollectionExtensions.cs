using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Scripts.Container;

/// <summary>
/// DI registration extensions for container-based script execution.
/// </summary>
public static class ContainerScriptServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IScriptRunner"/> backed by ephemeral K8s pods.
    /// Use this when hosting the script runner as a standalone service (e.g. RockBot.Scripts.Bridge).
    /// </summary>
    public static IServiceCollection AddContainerScriptRunner(
        this IServiceCollection services,
        Action<ContainerScriptOptions>? configure = null)
    {
        var options = new ContainerScriptOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.TryAddSingleton<IKubernetes>(_ =>
            new Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig()));

        services.AddSingleton<IScriptRunner, ContainerScriptRunner>();

        return services;
    }

    /// <summary>
    /// Registers the container script handler and subscribes to "script.invoke".
    /// Also registers a ScriptToolExecutor in the tool registry for LLM tool invocation.
    /// </summary>
    public static AgentHostBuilder AddContainerScriptHandler(
        this AgentHostBuilder builder,
        Action<ContainerScriptOptions>? configure = null)
    {
        var options = new ContainerScriptOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        // Register K8s client if not already registered
        builder.Services.TryAddSingleton<IKubernetes>(_ =>
            new Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig()));

        builder.Services.AddSingleton<ContainerScriptRunner>();

        // Register message handler for script.invoke topic
        builder.HandleMessage<ScriptInvokeRequest, ContainerScriptHandler>();
        builder.SubscribeTo("script.invoke");

        // Register script tool executor adapter for tool.invoke integration
        builder.Services.AddSingleton<ScriptToolExecutor>();

        return builder;
    }
}

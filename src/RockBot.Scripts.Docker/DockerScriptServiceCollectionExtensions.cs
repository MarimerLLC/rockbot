using Docker.DotNet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RockBot.Host;

namespace RockBot.Scripts.Docker;

/// <summary>
/// DI registration extensions for Docker-based script execution.
/// </summary>
public static class DockerScriptServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IScriptRunner"/> backed by ephemeral Docker containers directly.
    /// For the agent-side integration that delegates to the Script Manager via the
    /// message bus, use <c>AddRemoteScriptRunner()</c> from <c>RockBot.Scripts.Remote</c>.
    /// </summary>
    public static IServiceCollection AddDockerScriptRunner(
        this IServiceCollection services,
        Action<DockerScriptOptions>? configure = null)
    {
        var options = new DockerScriptOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.TryAddSingleton<IDockerClient>(_ => BuildDockerClient());

        services.AddSingleton<IScriptRunner, DockerScriptRunner>();

        return services;
    }

    /// <summary>
    /// Registers the Docker script handler and subscribes to "script.invoke".
    /// Used by the Script Manager to bridge RabbitMQ requests to ephemeral Docker containers.
    /// </summary>
    public static AgentHostBuilder AddDockerScriptHandler(
        this AgentHostBuilder builder,
        Action<DockerScriptOptions>? configure = null)
    {
        var options = new DockerScriptOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        builder.Services.TryAddSingleton<IDockerClient>(_ => BuildDockerClient());

        builder.Services.AddSingleton<DockerScriptRunner>();

        builder.HandleMessage<ScriptInvokeRequest, DockerScriptHandler>();
        builder.SubscribeTo("script.invoke");

        return builder;
    }

    /// <summary>
    /// Builds a Docker client using platform auto-detection.
    /// Unix: /var/run/docker.sock, Windows: npipe://./pipe/docker_engine.
    /// </summary>
    private static IDockerClient BuildDockerClient()
    {
        return new DockerClientConfiguration().CreateClient();
    }
}

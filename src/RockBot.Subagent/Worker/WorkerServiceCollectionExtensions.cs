using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Subagent.Worker;

/// <summary>
/// DI registration for the worker subsystem — the lean rung between wisps and subagents.
/// </summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>
    /// Adds worker spawning support and the <c>spawn_workers</c> tool.
    /// </summary>
    public static AgentHostBuilder AddWorkers(
        this AgentHostBuilder builder,
        Action<WorkerOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<WorkerOptions>(_ => { });

        builder.Services.AddSingleton<IWorkerManager, WorkerManager>();
        builder.Services.AddTransient<IWorkerRunner, WorkerRunner>();
        builder.Services.AddHostedService<WorkerToolRegistrar>();
        builder.Services.AddSingleton<IToolSkillProvider, WorkerToolSkillProvider>();

        return builder;
    }
}

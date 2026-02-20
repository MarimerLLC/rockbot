using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// DI registration extensions for scheduling tools.
/// </summary>
public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scheduler service, file-based task store, scheduled task handler,
    /// and the three LLM-callable tools: <c>schedule_task</c>, <c>cancel_scheduled_task</c>,
    /// and <c>list_scheduled_tasks</c>.
    /// </summary>
    public static AgentHostBuilder AddSchedulingTools(this AgentHostBuilder builder)
    {
        // Core scheduling: store, service, pipeline handler
        builder.AddScheduling();

        // Skill guide for the LLM
        builder.Services.AddSingleton<IToolSkillProvider, SchedulingToolSkillProvider>();

        // Hosted service that registers the tools at startup
        builder.Services.AddHostedService<SchedulingToolRegistrar>();

        return builder;
    }
}

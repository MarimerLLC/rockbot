using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RockBot.Host;

/// <summary>
/// Extension methods for adding scheduling support to an agent host.
/// </summary>
public static class AgentHostBuilderSchedulingExtensions
{
    /// <summary>
    /// Registers the scheduler service, task store, and task handler.
    /// The agent can then fire scheduled tasks through its LLM pipeline.
    /// Call <c>AddSchedulingTools()</c> in RockBot.Tools.Scheduling to also
    /// register the LLM-callable tools (schedule_task, cancel_scheduled_task, etc.).
    /// </summary>
    public static AgentHostBuilder AddScheduling(this AgentHostBuilder builder)
    {
        builder.Services.AddSingleton<IScheduledTaskStore, FileScheduledTaskStore>();

        // Register SchedulerService as a singleton that satisfies both ISchedulerService
        // and IHostedService through the same instance.
        builder.Services.AddSingleton<SchedulerService>();
        builder.Services.AddSingleton<ISchedulerService>(
            sp => sp.GetRequiredService<SchedulerService>());
        builder.Services.AddSingleton<IHostedService>(
            sp => sp.GetRequiredService<SchedulerService>());

        builder.HandleMessage<ScheduledTaskMessage, ScheduledTaskHandler>();

        return builder;
    }
}

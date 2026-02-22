using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Extension methods for adding scheduling support to an agent host.
/// </summary>
public static class AgentHostBuilderSchedulingExtensions
{
    /// <summary>
    /// Registers the core scheduling infrastructure: the cron timer service
    /// (<see cref="SchedulerService"/>) and the file-backed task store
    /// (<see cref="FileScheduledTaskStore"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method deliberately does <b>not</b> register a
    /// <c>IMessageHandler&lt;ScheduledTaskMessage&gt;</c>. The handler is intentionally
    /// left to the host application because it needs access to that agent's specific
    /// tool set and system-prompt context — things the framework library cannot know
    /// in advance. A code-review agent, a calendar agent, and a general-purpose agent
    /// all have different tools they should be able to use when a scheduled task fires.
    /// </para>
    /// <para>
    /// After calling this method (or <c>AddSchedulingTools()</c> which calls it),
    /// register your own handler in the host application:
    /// <code>
    /// agent.HandleMessage&lt;ScheduledTaskMessage, MyScheduledTaskHandler&gt;();
    /// </code>
    /// </para>
    /// <para>
    /// Call <c>AddSchedulingTools()</c> in <c>RockBot.Tools.Scheduling</c> to also
    /// register the three LLM-callable tools: <c>schedule_task</c>,
    /// <c>cancel_scheduled_task</c>, and <c>list_scheduled_tasks</c>.
    /// </para>
    /// </remarks>
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

        // Note: ScheduledTaskMessage handler is intentionally NOT registered here.
        // The host application (e.g. RockBot.Cli) must register its own handler so it
        // can supply the full tool set and a context-appropriate system prompt.

        return builder;
    }

    /// <summary>
    /// Registers <see cref="HeartbeatBootstrapService"/> which automatically creates
    /// the heartbeat-patrol scheduled task on startup if it does not already exist.
    /// </summary>
    public static AgentHostBuilder AddHeartbeatBootstrap(
        this AgentHostBuilder builder,
        Action<HeartbeatBootstrapOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<HeartbeatBootstrapOptions>(_ => { });

        builder.Services.AddSingleton<IHostedService, HeartbeatBootstrapService>();

        return builder;
    }
}

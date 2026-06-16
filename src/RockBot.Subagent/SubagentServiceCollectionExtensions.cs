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
        // Build a snapshot of the options now so we can read MaxConcurrentSubagents
        // when sizing the result-topic dispatch concurrency below. The configured
        // delegate still runs at DI build time for the runtime-resolved options.
        var optionsSnapshot = new SubagentOptions();
        configure?.Invoke(optionsSnapshot);

        if (configure is not null)
            builder.Services.Configure(configure);
        else
            builder.Services.Configure<SubagentOptions>(_ => { });

        // Core infrastructure
        builder.Services.AddSingleton<ISubagentManager, SubagentManager>();
        // Expose the same singleton as the A2A fold-back resolver seam (RockBot.A2A
        // cannot reference RockBot.Subagent directly).
        builder.Services.AddSingleton<ISubagentSessionResolver>(
            sp => (SubagentManager)sp.GetRequiredService<ISubagentManager>());
        builder.Services.AddSingleton<SubagentResultGate>();
        builder.Services.AddTransient<SubagentRunner>();

        // Message handlers for primary agent side
        builder.HandleMessage<SubagentProgressMessage, SubagentProgressHandler>();
        builder.HandleMessage<SubagentResultMessage, SubagentResultHandler>();
        var agentName = builder.Identity.Name;
        builder.SubscribeTo($"{SubagentTopics.Progress}.{agentName}");

        // Result topic must allow concurrent dispatch so sibling SubagentResultHandler
        // invocations can each enter SubagentResultGate.AccumulateAsync simultaneously.
        // Without this, the first handler's wait-for-siblings loop blocks the channel
        // and queued sibling results are delivered serially after each Phase 2 finishes,
        // producing one solo synthesis (and one final UI bubble) per subagent instead
        // of one consolidated synthesis per batch.
        // Size: MaxConcurrentSubagents (one slot per potential sibling) + 1 buffer.
        var resultDispatchConcurrency = Math.Max(2, optionsSnapshot.MaxConcurrentSubagents + 1);
        builder.SubscribeTo($"{SubagentTopics.Result}.{agentName}", resultDispatchConcurrency);

        // Tool registrar (registers spawn_subagent, cancel_subagent, list_subagents)
        builder.Services.AddHostedService<SubagentToolRegistrar>();

        // Skill guide
        builder.Services.AddSingleton<IToolSkillProvider, SubagentToolSkillProvider>();

        return builder;
    }
}

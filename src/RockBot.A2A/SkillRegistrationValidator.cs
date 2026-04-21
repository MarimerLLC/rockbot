using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.A2A;

/// <summary>
/// Hosted service that runs at startup to (1) validate the agent's skill
/// registration is consistent and (2) populate <see cref="A2AOptions.Card"/>.Skills
/// from the registered <see cref="IAgentSkillHandler"/> set. Registered automatically
/// by <see cref="A2ASkillHandlerExtensions.AddSkillHandler{T}"/>.
/// </summary>
/// <remarks>
/// This is registered as an <see cref="IHostedService"/> in <c>AddA2A</c> and runs
/// before <c>AgentDiscoveryService</c>, so the agent card announced on the bus and
/// served from the HTTP gateway already reflects the registered skills.
/// </remarks>
internal sealed class SkillRegistrationValidator(
    IServiceProvider rootProvider,
    A2AOptions options,
    ILogger<SkillRegistrationValidator> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = rootProvider.CreateScope();
        var sp = scope.ServiceProvider;

        var skillHandlers = sp.GetServices<IAgentSkillHandler>().ToList();
        if (skillHandlers.Count == 0)
        {
            // No skill-handler registrations — agent is using the traditional
            // single-IAgentTaskHandler model. Nothing to validate or auto-populate.
            return Task.CompletedTask;
        }

        var taskHandlers = sp.GetServices<IAgentTaskHandler>().ToList();
        var nonDispatcher = taskHandlers
            .Where(h => h is not SkillDispatchingTaskHandler)
            .ToList();
        if (nonDispatcher.Count > 0)
        {
            throw new InvalidOperationException(
                "Agent has both IAgentSkillHandler registrations and a custom " +
                $"IAgentTaskHandler ({string.Join(", ", nonDispatcher.Select(h => h.GetType().FullName))}). " +
                "Pick one model: either register IAgentSkillHandler implementations via " +
                "AddSkillHandler<T>(), or register a single IAgentTaskHandler for custom dispatch.");
        }

        MergeSkillsIntoCard(skillHandlers);

        logger.LogInformation(
            "Skill dispatch wired up: {Count} skill handler(s) registered ({Ids})",
            skillHandlers.Count,
            string.Join(", ", skillHandlers.Select(h => h.Skill.Id)));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void MergeSkillsIntoCard(IReadOnlyList<IAgentSkillHandler> handlers)
    {
        if (options.Card is null)
            return;

        var existing = options.Card.Skills is null
            ? new List<AgentSkill>()
            : [.. options.Card.Skills];

        var seen = new HashSet<string>(
            existing.Select(s => s.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (var handler in handlers)
        {
            if (seen.Add(handler.Skill.Id))
                existing.Add(handler.Skill);
        }

        options.Card = options.Card with { Skills = existing };
    }
}

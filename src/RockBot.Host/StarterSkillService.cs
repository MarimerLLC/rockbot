using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// On startup, seeds the skill store with a skill for each registered
/// <see cref="IToolSkillProvider"/> that doesn't already have one.
///
/// This makes tool-service guides (memory, MCP, web, etc.) discoverable through
/// the normal skill injection paths — session index injection and per-turn BM25
/// recall — rather than requiring the agent to proactively call list_tool_guides.
///
/// Seeding is intentionally additive: an existing skill is never overwritten, so
/// the agent (or dream cycle) can refine the content over time without it being
/// reset on every restart.
/// </summary>
internal sealed class StarterSkillService : IHostedService
{
    private readonly ISkillStore? _skillStore;
    private readonly IReadOnlyList<IToolSkillProvider> _providers;
    private readonly ILogger<StarterSkillService> _logger;

    public StarterSkillService(
        IEnumerable<ISkillStore> skillStores,
        IEnumerable<IToolSkillProvider> providers,
        ILogger<StarterSkillService> logger)
    {
        _skillStore = skillStores.FirstOrDefault();
        _providers = providers.ToList();
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_skillStore is null || _providers.Count == 0)
            return;

        var seeded = 0;
        foreach (var provider in _providers)
        {
            var existing = await _skillStore.GetAsync(provider.Name);
            if (existing is not null)
            {
                _logger.LogDebug("StarterSkillService: skill '{Name}' already exists; skipping", provider.Name);
                continue;
            }

            var skill = new Skill(
                Name: provider.Name,
                Summary: provider.Summary,
                Content: provider.GetDocument(),
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                LastUsedAt: null);

            await _skillStore.SaveAsync(skill);
            seeded++;
            _logger.LogInformation("StarterSkillService: seeded starter skill '{Name}'", provider.Name);
        }

        _logger.LogInformation(
            "StarterSkillService: startup complete — {Seeded} skill(s) seeded, {Existing} already present",
            seeded, _providers.Count - seeded);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

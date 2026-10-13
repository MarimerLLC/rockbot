using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// On startup, syncs the skill store with a skill for each registered
/// <see cref="IToolSkillProvider"/>: the starter skills.
///
/// This makes tool-service guides (memory, MCP, web, etc.) discoverable through
/// the normal skill injection paths — session index injection and per-turn BM25
/// recall — rather than requiring the agent to proactively call list_tool_guides.
///
/// Code owns these skills (#699). Each is stored with <see cref="Skill.CodeOwned"/> set, and a
/// stored copy whose content or summary differs from the provider's — an edited copy included —
/// is overwritten from code. The agent's skill tools and the dream passes refuse to change them;
/// lessons about a guide go to a <c>{name}-learned</c> companion skill. A flagged skill whose
/// guide is no longer registered is released back to an ordinary skill.
/// </summary>
internal sealed class StarterSkillService : IHostedService
{
    private readonly ISkillStore? _skillStore;
    private readonly IReadOnlyList<IToolSkillProvider> _providers;
    private readonly AgentHostOptions _hostOptions;
    private readonly ILogger<StarterSkillService> _logger;

    public StarterSkillService(
        IEnumerable<ISkillStore> skillStores,
        IEnumerable<IToolSkillProvider> providers,
        ILogger<StarterSkillService> logger,
        IOptions<AgentHostOptions>? hostOptions = null)
    {
        _skillStore = skillStores.FirstOrDefault();
        _providers = providers.ToList();
        _hostOptions = hostOptions?.Value ?? new AgentHostOptions();
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_hostOptions.EnableSkillInjection)
        {
            _logger.LogInformation(
                "StarterSkillService: skill injection is disabled; skipping starter-skill seeding");
            return;
        }

        if (_skillStore is null)
            return;

        int seeded = 0, resynced = 0;
        foreach (var provider in _providers)
        {
            var content = provider.GetDocument();
            var existing = await _skillStore.GetAsync(provider.Name);

            if (existing is not null
                && existing.CodeOwned
                && existing.Content == content
                && existing.Summary == provider.Summary)
            {
                _logger.LogDebug("StarterSkillService: skill '{Name}' matches code; skipping", provider.Name);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            // A re-sync keeps the skill's history, cross-references and attached resources.
            var skill = existing is null
                ? new Skill(
                    Name: provider.Name,
                    Summary: provider.Summary,
                    Content: content,
                    CreatedAt: now,
                    UpdatedAt: now,
                    LastUsedAt: null,
                    CodeOwned: true)
                : existing with
                {
                    Summary = provider.Summary,
                    Content = content,
                    UpdatedAt = now,
                    CodeOwned = true
                };

            try
            {
                await _skillStore.SaveAsync(skill);
                if (existing is null)
                {
                    seeded++;
                    _logger.LogInformation("StarterSkillService: seeded starter skill '{Name}'", provider.Name);
                }
                else
                {
                    resynced++;
                    _logger.LogInformation("StarterSkillService: re-synced starter skill '{Name}' from code", provider.Name);
                }
            }
            catch (Exception ex)
            {
                // Seeding is best-effort — a starter skill failing to save must not crash the host.
                _logger.LogWarning(ex,
                    "StarterSkillService: failed to save starter skill '{Name}'; continuing",
                    provider.Name);
            }
        }

        var released = await ReleaseOrphansAsync();

        _logger.LogInformation(
            "StarterSkillService: startup complete — {Seeded} skill(s) seeded, {Resynced} re-synced, " +
            "{Unchanged} already present, {Released} released",
            seeded, resynced, _providers.Count - seeded - resynced, released);
    }

    /// <summary>
    /// A flagged skill whose guide is no longer registered (its tools were turned off, say)
    /// becomes an ordinary skill rather than staying locked with nothing to re-sync it.
    /// </summary>
    private async Task<int> ReleaseOrphansAsync()
    {
        var guideNames = new HashSet<string>(_providers.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var released = 0;
        try
        {
            foreach (var skill in await _skillStore!.ListAsync())
            {
                if (!skill.CodeOwned || guideNames.Contains(skill.Name))
                    continue;

                await _skillStore.SaveAsync(skill with { CodeOwned = false });
                released++;
                _logger.LogInformation(
                    "StarterSkillService: released skill '{Name}' — no tool guide registers it any more",
                    skill.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StarterSkillService: failed to release orphaned starter skills; continuing");
        }
        return released;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

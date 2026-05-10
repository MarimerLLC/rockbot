using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Tools.Mcp;

/// <summary>
/// One-shot startup migration that renames legacy top-level skills matching a
/// known MCP <c>server_name</c> to <c>mcp/{server-name}</c>. Cleans up drift
/// from before the naming convention was reinforced (issue #381).
///
/// The service waits for <see cref="McpServerIndex"/> to be populated by the
/// bridge — up to <see cref="WaitForIndex"/> total — and then enumerates the
/// skill store. Idempotent on subsequent runs: skills already namespaced are
/// left alone; if both old and new names exist, the legacy top-level entry is
/// removed and the namespaced entry is preserved.
/// </summary>
/// <remarks>
/// REMOVE AFTER MIGRATION: this service exists to clean up pre-#381 drift in
/// the maintainer's live cluster — RockBot has a single production instance
/// today. Once the live skill store has been migrated and verified (no more
/// top-level skills matching MCP server names), delete this file along with
/// its registration in <c>McpServiceCollectionExtensions.AddMcpToolProxy</c>
/// and the matching tests in <c>McpSkillNameMigrationServiceTests</c>.
/// The naming convention itself is enforced going forward by directives, so
/// the migration step is not load-bearing once the one-time cleanup is done.
/// </remarks>
internal sealed class McpSkillNameMigrationService : IHostedService
{
    private static readonly TimeSpan WaitForIndex = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ISkillStore _skillStore;
    private readonly McpServerIndex _index;
    private readonly ILogger<McpSkillNameMigrationService> _logger;

    private CancellationTokenSource? _cts;

    public McpSkillNameMigrationService(
        ISkillStore skillStore,
        McpServerIndex index,
        ILogger<McpSkillNameMigrationService> logger)
    {
        _skillStore = skillStore;
        _index = index;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = RunInBackgroundAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            var deadline = DateTime.UtcNow.Add(WaitForIndex);
            while (DateTime.UtcNow < deadline && _index.Servers.Count == 0)
            {
                try { await Task.Delay(PollInterval, ct); }
                catch (OperationCanceledException) { return; }
            }

            var servers = _index.Servers;
            if (servers.Count == 0)
            {
                _logger.LogInformation(
                    "MCP server index did not populate within {Timeout}s; skipping skill name migration",
                    WaitForIndex.TotalSeconds);
                return;
            }

            await MigrateAsync(servers, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP skill name migration failed");
        }
    }

    /// <summary>
    /// Renames top-level skills whose name matches a known <paramref name="servers"/>
    /// entry's <c>server_name</c> (case-insensitive) to <c>mcp/{server-name}</c>.
    /// </summary>
    /// <remarks>
    /// Exposed at <c>internal</c> so tests can drive migration directly without
    /// waiting on the index-population polling path.
    /// </remarks>
    internal async Task<MigrationSummary> MigrateAsync(
        IReadOnlyList<McpServerSummary> servers,
        CancellationToken ct)
    {
        var serverNames = new HashSet<string>(
            servers.Select(s => s.ServerName),
            StringComparer.OrdinalIgnoreCase);

        var skills = await _skillStore.ListAsync();

        int renamed = 0, removedDuplicate = 0;
        foreach (var skill in skills)
        {
            ct.ThrowIfCancellationRequested();

            // Only flat top-level names are candidates. Skills already nested
            // anywhere (including under mcp/) are ignored.
            if (skill.Name.Contains('/')) continue;
            if (!serverNames.Contains(skill.Name)) continue;

            var newName = $"mcp/{skill.Name.ToLowerInvariant()}";

            var existing = await _skillStore.GetAsync(newName);
            if (existing is not null)
            {
                await _skillStore.DeleteAsync(skill.Name);
                removedDuplicate++;
                _logger.LogInformation(
                    "Removed duplicate top-level skill '{Old}' (already migrated to '{New}')",
                    skill.Name, newName);
                continue;
            }

            var migrated = skill with
            {
                Name = newName,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _skillStore.SaveAsync(migrated);
            await _skillStore.DeleteAsync(skill.Name);
            renamed++;
            _logger.LogInformation(
                "Migrated MCP server skill '{Old}' to '{New}'",
                skill.Name, newName);
        }

        if (renamed > 0 || removedDuplicate > 0)
        {
            _logger.LogInformation(
                "MCP skill name migration: {Renamed} renamed, {RemovedDuplicate} duplicate(s) removed",
                renamed, removedDuplicate);
        }

        return new MigrationSummary(renamed, removedDuplicate);
    }

    internal readonly record struct MigrationSummary(int Renamed, int RemovedDuplicate);
}

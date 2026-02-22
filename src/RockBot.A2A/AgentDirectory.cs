using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.A2A;

/// <summary>
/// Thread-safe agent directory with optional file persistence.
/// Entries are keyed by agent name and carry a last-seen timestamp so stale
/// registrations (agents that stopped without deregistering) can be pruned on
/// startup via <see cref="A2AOptions.DirectoryEntryTtl"/>.
///
/// Implements <see cref="IHostedService"/> to load the persisted file at startup
/// and flush on shutdown.
/// </summary>
internal sealed class AgentDirectory(
    A2AOptions options,
    ILogger<AgentDirectory> logger) : IAgentDirectory, IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, AgentDirectoryEntry> _agents =
        new(StringComparer.OrdinalIgnoreCase);

    // Debounce: only one pending write at a time
    private volatile bool _writePending;

    // -------------------------------------------------------------------------
    // IHostedService

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var path = ResolvePath(options.DirectoryPersistencePath);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(json, JsonOptions);
            if (entries is null) return;

            var cutoff = DateTimeOffset.UtcNow - options.DirectoryEntryTtl;
            var loaded = 0;
            var pruned = 0;

            foreach (var e in entries)
            {
                if (e.Card is null) continue;

                if (e.LastSeenAt < cutoff)
                {
                    pruned++;
                    continue;
                }

                _agents[e.Card.AgentName] = new AgentDirectoryEntry
                {
                    Card = e.Card,
                    LastSeenAt = e.LastSeenAt
                };
                loaded++;
            }

            logger.LogInformation(
                "Loaded {Loaded} agent(s) from directory ({Pruned} stale entries pruned, TTL={Ttl}h)",
                loaded, pruned, options.DirectoryEntryTtl.TotalHours);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load agent directory from {Path}", path);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        FlushAsync(cancellationToken);

    // -------------------------------------------------------------------------
    // IAgentDirectory

    public AgentCard? GetAgent(string agentName) =>
        _agents.TryGetValue(agentName, out var entry) ? entry.Card : null;

    public IReadOnlyList<AgentCard> GetAllAgents() =>
        _agents.Values.Select(e => e.Card).ToList();

    public IReadOnlyList<AgentCard> FindBySkill(string skillId) =>
        _agents.Values
            .Where(e => e.Card.Skills?.Any(
                s => string.Equals(s.Id, skillId, StringComparison.OrdinalIgnoreCase)) == true)
            .Select(e => e.Card)
            .ToList();

    public IReadOnlyList<AgentDirectoryEntry> GetAllEntries() =>
        _agents.Values.ToList();

    // -------------------------------------------------------------------------
    // Write methods (called by AgentDiscoveryService)

    internal void AddOrUpdate(AgentCard card)
    {
        _agents[card.AgentName] = new AgentDirectoryEntry
        {
            Card = card,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        ScheduleWrite();
    }

    internal void Remove(string agentName)
    {
        if (_agents.TryRemove(agentName, out _))
        {
            logger.LogInformation("Removed deregistered agent '{AgentName}' from directory", agentName);
            ScheduleWrite();
        }
    }

    // -------------------------------------------------------------------------
    // Persistence helpers

    private void ScheduleWrite()
    {
        if (_writePending) return;
        _writePending = true;
        _ = Task.Run(async () =>
        {
            try { await FlushAsync(CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to persist agent directory"); }
            finally { _writePending = false; }
        });
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var path = ResolvePath(options.DirectoryPersistencePath);
        if (string.IsNullOrEmpty(path)) return;

        var entries = _agents.Values
            .Select(e => new PersistedEntry { Card = e.Card, LastSeenAt = e.LastSeenAt })
            .ToList();

        var json = JsonSerializer.Serialize(entries, JsonOptions);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, json, ct);

        logger.LogDebug("Persisted {Count} agent(s) to {Path}", entries.Count, path);
    }

    private static string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(AppContext.BaseDirectory, path);
    }

    // DTO for JSON serialization — avoids polluting AgentDirectoryEntry with serializer concerns
    private sealed class PersistedEntry
    {
        public AgentCard? Card { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }
}

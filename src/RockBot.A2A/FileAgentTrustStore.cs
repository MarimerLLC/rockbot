using System.Collections.Concurrent;
using System.Text.Json;

namespace RockBot.A2A;

/// <summary>
/// File-backed trust store that persists <see cref="AgentTrustEntry"/> records as JSON.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/> with debounced writes.
/// </summary>
internal sealed class FileAgentTrustStore : IAgentTrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, AgentTrustEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _loaded;

    public FileAgentTrustStore(string? filePath)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
    }

    public async Task<AgentTrustEntry> GetOrCreateAsync(string agentId, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);

        if (_entries.TryGetValue(agentId, out var existing))
            return existing;

        var entry = new AgentTrustEntry
        {
            AgentId = agentId,
            Level = AgentTrustLevel.Observe,
            FirstSeen = DateTimeOffset.UtcNow,
            LastInteraction = DateTimeOffset.UtcNow,
            InteractionCount = 0
        };
        var actual = _entries.GetOrAdd(agentId, entry);
        if (ReferenceEquals(actual, entry))
            await PersistAsync(ct);
        return actual;
    }

    public async Task UpdateAsync(AgentTrustEntry entry, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        _entries[entry.AgentId] = entry;
        await PersistAsync(ct);
    }

    public async Task<IReadOnlyList<AgentTrustEntry>> ListAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        return _entries.Values.ToList();
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            if (_loaded) return;

            if (_filePath is not null && File.Exists(_filePath))
            {
                var json = await File.ReadAllTextAsync(_filePath, ct);
                var entries = JsonSerializer.Deserialize<List<AgentTrustEntry>>(json, JsonOptions);
                if (entries is not null)
                {
                    foreach (var entry in entries)
                        _entries.TryAdd(entry.AgentId, entry);
                }
            }

            _loaded = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_filePath is null) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            var entries = _entries.Values.ToList();
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_filePath, json, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

using RockBot.Host;

namespace RockBot.AdvisorCouncil.Tests;

/// <summary>
/// Minimal <see cref="IWorkingMemory"/> for council tests. Backed by a dictionary, ignores
/// TTL (test runs are short), supports prefix-based listing — enough for the WM-mediated
/// council pipeline.
/// </summary>
internal sealed class InMemoryWorkingMemory : IWorkingMemory
{
    private readonly Dictionary<string, WorkingMemoryEntry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, WorkingMemoryEntry> Entries => _entries;

    public Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(ttl ?? TimeSpan.FromHours(1));
        _entries[key] = new WorkingMemoryEntry(key, value, now, expires, category, tags);
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key) =>
        Task.FromResult(_entries.TryGetValue(key, out var e) ? e.Value : null);

    public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null)
    {
        IReadOnlyList<WorkingMemoryEntry> list = _entries.Values
            .Where(e => string.IsNullOrEmpty(prefix) || e.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        return Task.FromResult(list);
    }

    public Task DeleteAsync(string key)
    {
        _entries.Remove(key);
        return Task.CompletedTask;
    }

    public Task ClearAsync(string? prefix = null)
    {
        var toRemove = _entries.Keys
            .Where(k => string.IsNullOrEmpty(prefix) || k.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        foreach (var k in toRemove)
            _entries.Remove(k);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
        ListAsync(prefix);
}

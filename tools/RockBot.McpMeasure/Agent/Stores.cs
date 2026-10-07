using System.Collections.Concurrent;
using RockBot.Host;

namespace RockBot.McpMeasure.Agent;

/// <summary>Working memory that lives for the process; enough for the loop's tool-result chunking.</summary>
internal sealed class InMemoryWorkingMemory : IWorkingMemory
{
    private readonly ConcurrentDictionary<string, WorkingMemoryEntry> _entries = new(StringComparer.Ordinal);

    public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null)
    {
        var now = DateTimeOffset.UtcNow;
        _entries[key] = new WorkingMemoryEntry(key, value, now, now + (ttl ?? TimeSpan.FromHours(1)), category, tags);
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key) =>
        Task.FromResult(_entries.TryGetValue(key, out var e) ? e.Value : null);

    public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>(
            [.. _entries.Values.Where(e => prefix is null || e.Key.StartsWith(prefix, StringComparison.Ordinal))]);

    public Task DeleteAsync(string key)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task ClearAsync(string? prefix = null)
    {
        foreach (var key in _entries.Keys.Where(k => prefix is null || k.StartsWith(prefix, StringComparison.Ordinal)))
            _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
        ListAsync(prefix);
}

internal sealed class NullFeedbackStore : IFeedbackStore
{
    public Task AppendAsync(FeedbackEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<FeedbackEntry>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);

    public Task<IReadOnlyList<FeedbackEntry>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FeedbackEntry>>([]);
}

internal sealed class NullSkillStore : ISkillStore
{
    public Task SaveAsync(Skill skill) => Task.CompletedTask;
    public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
    public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
    public Task DeleteAsync(string name) => Task.CompletedTask;

    public Task<IReadOnlyList<Skill>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
        Task.FromResult<IReadOnlyList<Skill>>([]);
}

internal sealed class NullConversationMemory : IConversationMemory
{
    public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversationTurn>>([]);

    public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

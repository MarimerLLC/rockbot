using RockBot.Host;

namespace RockBot.ResearchAgent;

/// <summary>
/// A research run's view of the shared working memory: it reaches only the run's own prefixes
/// (its <c>research/{runId}/</c> namespace and its tool-result stash, <c>stash/{runId}/</c>).
/// </summary>
/// <remarks>
/// <para>
/// The research server is long-lived and runs several research questions at once over one
/// singleton store. <see cref="RockBot.Memory.WorkingMemoryTools"/> treats its namespace as a
/// default, not a boundary: it accepts full-path keys and arbitrary search prefixes. The model
/// reads untrusted web pages, so without this view a page could steer one run into reading or
/// overwriting another caller's findings, or into writing outside the prefixes the run clears.
/// </para>
/// <para>
/// Outside the run's prefixes, reads find nothing, listings and searches leave the entries out,
/// deletes and clears do nothing, and writes and edits fail with a message the model can read.
/// </para>
/// </remarks>
internal sealed class RunScopedWorkingMemory : IWorkingMemory
{
    private readonly IWorkingMemory _inner;
    private readonly string[] _allowed;

    /// <param name="inner">The shared store.</param>
    /// <param name="allowedPrefixes">The run's prefixes; a trailing <c>/</c> is added when missing.</param>
    public RunScopedWorkingMemory(IWorkingMemory inner, params string[] allowedPrefixes)
    {
        _inner = inner;
        _allowed = [.. allowedPrefixes.Select(p => p.EndsWith('/') ? p : p + "/")];
    }

    /// <summary>True when <paramref name="key"/> is inside one of the run's prefixes.</summary>
    internal bool InScope(string? key) =>
        key is not null && _allowed.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The run prefix a listing, search or clear with <paramref name="prefix"/> may use: the
    /// prefix itself when it lies inside the run, or the run prefix it names without its trailing
    /// slash (<c>research/run1</c> must not also match <c>research/run12/…</c>). Null otherwise.
    /// </summary>
    private string? ScopedPrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return null;
        if (InScope(prefix))
            return prefix;
        return _allowed.FirstOrDefault(p => string.Equals(p, prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    private InvalidOperationException OutOfScope(string key) =>
        new($"Working memory key '{key}' is outside this research run. " +
            $"Use a short key (it goes in this run's namespace) or a key under: {string.Join(", ", _allowed)}");

    public Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null) =>
        InScope(key) ? _inner.SetAsync(key, value, ttl, category, tags) : throw OutOfScope(key);

    public Task SetAsync(string key, string value, TimeSpan? ttl, string? category,
        IReadOnlyList<string>? tags, string? writer) =>
        InScope(key) ? _inner.SetAsync(key, value, ttl, category, tags, writer) : throw OutOfScope(key);

    public Task<WorkingMemoryWriteResult> TrySetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null, string? writer = null) =>
        InScope(key) ? _inner.TrySetAsync(key, value, ttl, category, tags, writer) : throw OutOfScope(key);

    public Task<string?> GetAsync(string key) =>
        InScope(key) ? _inner.GetAsync(key) : Task.FromResult<string?>(null);

    public Task<WorkingMemoryEntry?> GetEntryAsync(string key) =>
        InScope(key) ? _inner.GetEntryAsync(key) : Task.FromResult<WorkingMemoryEntry?>(null);

    public Task TouchAsync(string key) =>
        InScope(key) ? _inner.TouchAsync(key) : Task.CompletedTask;

    public Task<ContentEditResult> EditAsync(string key, string oldText, string newText, bool replaceAll = false) =>
        InScope(key)
            ? _inner.EditAsync(key, oldText, newText, replaceAll)
            : Task.FromResult(ContentEditResult.Failed(OutOfScope(key).Message));

    public async Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            var all = new List<WorkingMemoryEntry>();
            foreach (var p in _allowed)
                all.AddRange(await _inner.ListAsync(p));
            return all;
        }

        return ScopedPrefix(prefix) is { } scoped
            ? [.. (await _inner.ListAsync(scoped)).Where(e => InScope(e.Key))]
            : [];
    }

    public Task DeleteAsync(string key) =>
        InScope(key) ? _inner.DeleteAsync(key) : Task.CompletedTask;

    public async Task ClearAsync(string? prefix = null)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            foreach (var p in _allowed)
                await _inner.ClearAsync(p);
            return;
        }

        if (ScopedPrefix(prefix) is { } scoped)
            await _inner.ClearAsync(scoped);
    }

    public async Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            var all = new List<WorkingMemoryEntry>();
            foreach (var p in _allowed)
                all.AddRange(await _inner.SearchAsync(criteria, p));
            return all;
        }

        return ScopedPrefix(prefix) is { } scoped
            ? [.. (await _inner.SearchAsync(criteria, scoped)).Where(e => InScope(e.Key))]
            : [];
    }
}

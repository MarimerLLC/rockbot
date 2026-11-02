using RockBot.Host;

namespace RockBot.ResearchAgent.Tests;

/// <summary>
/// A research run's memory tools must not reach another run's entries: the model reads untrusted
/// pages, and concurrent runs share one store.
/// </summary>
[TestClass]
public class RunScopedWorkingMemoryTests
{
    private static async Task<(DictionaryWorkingMemory Store, RunScopedWorkingMemory Run1)> SeedAsync()
    {
        var store = new DictionaryWorkingMemory();
        await store.SetAsync("research/run1/notes", "mine");
        await store.SetAsync("stash/run1/call1", "my stash");
        await store.SetAsync("research/run12/notes", "neighbour with a longer id");
        await store.SetAsync("research/run2/notes", "someone else's");
        await store.SetAsync("stash/run2/call1", "someone else's stash");
        return (store, new RunScopedWorkingMemory(store, "research/run1/", "stash/run1"));
    }

    [TestMethod]
    public async Task ItsOwnEntries_ReadAndWriteNormally()
    {
        var (store, run) = await SeedAsync();

        Assert.AreEqual("mine", await run.GetAsync("research/run1/notes"));
        Assert.AreEqual("my stash", await run.GetAsync("stash/run1/call1"));

        await run.SetAsync("research/run1/findings", "new");
        Assert.AreEqual("new", await store.GetAsync("research/run1/findings"));
        Assert.IsNotNull(await run.GetEntryAsync("research/run1/findings"));
    }

    [TestMethod]
    public async Task AnotherRunsEntries_CantBeRead()
    {
        var (_, run) = await SeedAsync();

        Assert.IsNull(await run.GetAsync("research/run2/notes"));
        Assert.IsNull(await run.GetAsync("stash/run2/call1"));
        Assert.IsNull(await run.GetAsync("research/run12/notes"), "run1 is not a prefix of run12");
        Assert.IsNull(await run.GetEntryAsync("research/run2/notes"));
    }

    [TestMethod]
    public async Task WritesOutsideTheRun_FailAndChangeNothing()
    {
        var (store, run) = await SeedAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.SetAsync("research/run2/notes", "overwritten"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.SetAsync("session/abc/x", "escaped"));
        var edit = await run.EditAsync("research/run2/notes", "someone", "me");
        Assert.IsFalse(edit.IsSuccess);

        await run.DeleteAsync("research/run2/notes");
        await run.ClearAsync("research/run2");
        await run.ClearAsync("research");

        Assert.AreEqual("someone else's", await store.GetAsync("research/run2/notes"));
        Assert.IsNull(await store.GetAsync("session/abc/x"));
        Assert.AreEqual("neighbour with a longer id", await store.GetAsync("research/run12/notes"));
    }

    [TestMethod]
    public async Task ListingsAndSearches_ShowOnlyTheRun()
    {
        var (_, run) = await SeedAsync();

        CollectionAssert.AreEquivalent(
            new[] { "research/run1/notes", "stash/run1/call1" },
            (await run.ListAsync()).Select(e => e.Key).ToArray());
        CollectionAssert.AreEqual(
            new[] { "research/run1/notes" },
            (await run.ListAsync("research/run1")).Select(e => e.Key).ToArray(),
            "a prefix without its slash still means only this run, not run12");
        Assert.AreEqual(0, (await run.ListAsync("research")).Count);
        Assert.AreEqual(0, (await run.ListAsync("research/run2/")).Count);
        Assert.AreEqual(0, (await run.SearchAsync(new MemorySearchCriteria(), "stash")).Count);
        Assert.AreEqual(2, (await run.SearchAsync(new MemorySearchCriteria())).Count);
    }

    [TestMethod]
    public async Task ClearingTheRun_LeavesEveryoneElse()
    {
        var (store, run) = await SeedAsync();

        await run.ClearAsync();

        Assert.IsNull(await store.GetAsync("research/run1/notes"));
        Assert.IsNull(await store.GetAsync("stash/run1/call1"));
        Assert.IsNotNull(await store.GetAsync("research/run12/notes"));
        Assert.IsNotNull(await store.GetAsync("research/run2/notes"));
        Assert.IsNotNull(await store.GetAsync("stash/run2/call1"));
    }

    [TestMethod]
    [DataRow("https://Example.COM/report", "https://example.com/report", true)]
    [DataRow("https://example.com/Report", "https://example.com/report", false)]
    [DataRow("https://example.com/a?Q=1", "https://example.com/a?q=1", false)]
    [DataRow("https://example.com/a#intro", "https://example.com/a", true)]
    public void SourceDedup_IgnoresHostCaseAndFragments_ButNotPathCase(string first, string second, bool same)
    {
        var log = new SourceLog();
        log.Add(first, null);
        log.Add(second, null);

        Assert.AreEqual(same ? 1 : 2, log.Sources.Count);
        Assert.AreEqual(first, log.Sources[0].Url, "the first spelling read is the one reported");
    }

    /// <summary>A minimal case-insensitive prefix store, like the real one's key handling.</summary>
    private sealed class DictionaryWorkingMemory : IWorkingMemory
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

        public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null)
        {
            _entries[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(_entries.TryGetValue(key, out var v) ? v : null);

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([.. Matching(prefix)]);

        public Task DeleteAsync(string key)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string? prefix = null)
        {
            foreach (var e in Matching(prefix).ToList())
                _entries.Remove(e.Key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
            ListAsync(prefix);

        public Task<ContentEditResult> EditAsync(string key, string oldText, string newText, bool replaceAll = false)
        {
            if (!_entries.TryGetValue(key, out var v))
                return Task.FromResult(ContentEditResult.Failed("not found"));
            _entries[key] = v.Replace(oldText, newText);
            return Task.FromResult(ContentEditResult.Applied(1, oldText.Length, newText.Length));
        }

        private IEnumerable<WorkingMemoryEntry> Matching(string? prefix) =>
            _entries
                .Where(kv => string.IsNullOrEmpty(prefix) || kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(kv => new WorkingMemoryEntry(kv.Key, kv.Value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)));
    }
}

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// A full working-memory namespace (#801): regenerable content gives way, a refused write is
/// reported, and chunked content says how much of it was stored.
/// </summary>
[TestClass]
public sealed class WorkingMemoryFullNamespaceTests
{
    private MemoryCache _cache = null!;

    [TestInitialize]
    public void Setup() => _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

    [TestCleanup]
    public void Cleanup() => _cache.Dispose();

    private HybridCacheWorkingMemory Memory(int maxEntries, IList<string>? evictable = null)
    {
        var embedding = Options.Create(new EmbeddingOptions());
        return new HybridCacheWorkingMemory(
            _cache,
            Options.Create(new WorkingMemoryOptions { MaxEntriesPerNamespace = maxEntries, EvictableCategories = evictable }),
            embedding,
            new EmbeddingTextPreparer(embedding, NullLogger<EmbeddingTextPreparer>.Instance),
            NullLogger<HybridCacheWorkingMemory>.Instance);
    }

    private static async Task StorePageAsync(IWorkingMemory memory, string page, int chunks)
    {
        for (var i = 0; i < chunks; i++)
        {
            await memory.SetAsync($"session/s1/web-{page}-chunk{i}", $"{page} part {i}", category: "web");
            await Task.Delay(2); // distinct stored-at times
        }
        await memory.SetAsync($"session/s1/web-{page}-index", $"{page} outline", category: "web");
        await Task.Delay(2);
    }

    // ── The store ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task FullNamespace_EvictsTheOldestPage_Whole()
    {
        var memory = Memory(maxEntries: 8);
        await StorePageAsync(memory, "old", chunks: 3);    // 4 entries
        await StorePageAsync(memory, "newer", chunks: 3);  // 8 entries: full

        var write = await memory.TrySetAsync("session/s1/web-latest-chunk0", "latest part 0", category: "web");

        Assert.IsTrue(write.Stored);
        CollectionAssert.AreEquivalent(
            new[] { "session/s1/web-old-chunk0", "session/s1/web-old-chunk1", "session/s1/web-old-chunk2", "session/s1/web-old-index" },
            write.EvictedKeys!.ToArray(),
            "the whole oldest page goes, so nothing is left half there");
        Assert.IsNull(await memory.GetAsync("session/s1/web-old-chunk1"));
        Assert.AreEqual("newer part 2", await memory.GetAsync("session/s1/web-newer-chunk2"));
        Assert.AreEqual("latest part 0", await memory.GetAsync("session/s1/web-latest-chunk0"));
    }

    [TestMethod]
    public async Task FullNamespace_NeverEvictsWhatTheAgentSavedOnPurpose()
    {
        var memory = Memory(maxEntries: 3);
        await memory.SetAsync("session/s1/plan", "my plan");
        await memory.SetAsync("session/s1/notes", "my notes", category: "research");
        await StorePageAsync(memory, "page", chunks: 0); // just its index: full

        var write = await memory.TrySetAsync("session/s1/findings", "findings");

        Assert.IsTrue(write.Stored, "the page index made room");
        Assert.AreEqual("my plan", await memory.GetAsync("session/s1/plan"));
        Assert.AreEqual("my notes", await memory.GetAsync("session/s1/notes"));

        var refused = await memory.TrySetAsync("session/s1/more", "more");
        Assert.IsFalse(refused.Stored, "nothing regenerable is left to evict");
        StringAssert.Contains(refused.Reason, "full");
        Assert.IsNull(await memory.GetAsync("session/s1/more"));
    }

    [TestMethod]
    public async Task APage_BiggerThanTheRoom_DoesntEvictItsOwnChunks()
    {
        var memory = Memory(maxEntries: 3);
        await memory.SetAsync("session/s1/plan", "my plan");

        var results = new List<WorkingMemoryWriteResult>();
        for (var i = 0; i < 4; i++)
            results.Add(await memory.TrySetAsync($"session/s1/web-big-chunk{i}", $"big {i}", category: "web"));

        Assert.IsTrue(results[0].Stored && results[1].Stored);
        Assert.IsFalse(results[2].Stored, "the page's own chunks are not eviction candidates");
        Assert.AreEqual("big 0", await memory.GetAsync("session/s1/web-big-chunk0"));
    }

    [TestMethod]
    public async Task EvictionIsPerNamespace()
    {
        var memory = Memory(maxEntries: 2);
        await StorePageAsync(memory, "other", chunks: 1);   // session/s1
        await memory.SetAsync("session/s2/a", "a");
        await memory.SetAsync("session/s2/b", "b");

        var write = await memory.TrySetAsync("session/s2/c", "c");

        Assert.IsFalse(write.Stored, "s1's page can't make room in s2");
        Assert.AreEqual("other part 0", await memory.GetAsync("session/s1/web-other-chunk0"));
    }

    [TestMethod]
    public async Task NoEvictableCategories_RefusesAsBefore()
    {
        var memory = Memory(maxEntries: 2, evictable: []);
        await StorePageAsync(memory, "page", chunks: 1);

        var write = await memory.TrySetAsync("session/s1/web-next-chunk0", "next", category: "web");

        Assert.IsFalse(write.Stored);
    }

    [TestMethod]
    [DataRow("session/s1/web-x-chunk12", "session/s1/web-x")]
    [DataRow("session/s1/tool-search-ab12-index", "session/s1/tool-search-ab12")]
    [DataRow("session/s1/plan", "session/s1/plan")]
    public void ChunkGroup_StripsTheChunkSuffix(string key, string group) =>
        Assert.AreEqual(group, HybridCacheWorkingMemory.ChunkGroup(key));

    // ── ChunkedContentStore ─────────────────────────────────────────────────

    private static IReadOnlyList<ChunkInfo> Chunks(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new ChunkInfo($"Section {i}", $"content {i}", 2))];

    [TestMethod]
    public async Task ChunkedContentStore_AllStored_IsComplete()
    {
        var memory = Memory(maxEntries: 10);

        var result = await ChunkedContentStore.StoreAsync(memory, "session/s1/web-p", Chunks(3), TimeSpan.FromMinutes(5), "web");

        Assert.IsTrue(result.IsComplete);
        Assert.AreEqual(3, result.StoredKeys.Count);
        Assert.AreEqual("session/s1/web-p-index", result.IndexKey);
        Assert.IsNull(result.ShortfallNote);
    }

    [TestMethod]
    public async Task ChunkedContentStore_FullNamespace_ReportsTheShortfall()
    {
        var memory = Memory(maxEntries: 4);
        await memory.SetAsync("session/s1/plan", "my plan");
        await memory.SetAsync("session/s1/notes", "my notes");

        var result = await ChunkedContentStore.StoreAsync(memory, "session/s1/web-p", Chunks(5), TimeSpan.FromMinutes(5), "web");

        Assert.IsFalse(result.IsComplete);
        CollectionAssert.AreEqual(new[] { "session/s1/web-p-chunk0", "session/s1/web-p-chunk1" }, result.StoredKeys.ToArray());
        Assert.AreEqual(3, result.NotStored);
        Assert.IsNull(result.IndexKey, "no room left for the outline either");
        StringAssert.Contains(result.ShortfallNote, "only 2 of 5 chunk(s)");
        StringAssert.Contains(result.ShortfallNote, "NOT in working memory");
        Assert.AreEqual("content 1", await memory.GetAsync("session/s1/web-p-chunk1"), "what is listed is really there");
    }
}

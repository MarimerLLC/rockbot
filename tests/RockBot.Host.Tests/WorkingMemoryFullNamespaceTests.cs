using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Memory;

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

    private HybridCacheWorkingMemory Memory(int maxEntries = 50, int maxRegenerable = 400, IList<string>? evictable = null)
    {
        var embedding = Options.Create(new EmbeddingOptions());
        return new HybridCacheWorkingMemory(
            _cache,
            Options.Create(new WorkingMemoryOptions
            {
                MaxEntriesPerNamespace = maxEntries,
                MaxRegenerableEntriesPerNamespace = maxRegenerable,
                EvictableCategories = evictable
            }),
            embedding,
            new EmbeddingTextPreparer(embedding, NullLogger<EmbeddingTextPreparer>.Instance),
            NullLogger<HybridCacheWorkingMemory>.Instance);
    }

    private static async Task StorePageAsync(IWorkingMemory memory, string page, int chunks, bool pace = true)
    {
        for (var i = 0; i < chunks; i++)
        {
            await memory.SetAsync($"session/s1/web-{page}-chunk{i}", $"{page} part {i}", category: "web");
            if (pace) await Task.Delay(2); // distinct stored-at times
        }
        await memory.SetAsync($"session/s1/web-{page}-index", $"{page} outline", category: "web");
        if (pace) await Task.Delay(2);
    }

    private static string[] PageKeys(string page, int chunks) =>
        [.. Enumerable.Range(0, chunks).Select(i => $"session/s1/web-{page}-chunk{i}"), $"session/s1/web-{page}-index"];

    // ── The store: regenerable budget ───────────────────────────────────────

    [TestMethod]
    public async Task FullPageBudget_EvictsTheOldestUnreadPage_Whole()
    {
        var memory = Memory(maxRegenerable: 8);
        await StorePageAsync(memory, "old", chunks: 3);    // 4 entries
        await StorePageAsync(memory, "newer", chunks: 3);  // 8 entries: full

        var write = await memory.TrySetAsync("session/s1/web-latest-chunk0", "latest part 0", category: "web");

        Assert.IsTrue(write.Stored);
        CollectionAssert.AreEquivalent(
            PageKeys("old", 3),
            write.EvictedKeys!.ToArray(),
            "the whole oldest page goes, so nothing is left half there");
        Assert.IsNull(await memory.GetAsync("session/s1/web-old-chunk1"));
        Assert.AreEqual("newer part 2", await memory.GetAsync("session/s1/web-newer-chunk2"));
        Assert.AreEqual("latest part 0", await memory.GetAsync("session/s1/web-latest-chunk0"));
    }

    [TestMethod]
    public async Task Eviction_PrefersTheLeastRecentlyReadPage()
    {
        var memory = Memory(maxRegenerable: 12);
        await StorePageAsync(memory, "a", chunks: 3);
        await StorePageAsync(memory, "b", chunks: 3);
        await StorePageAsync(memory, "c", chunks: 3); // 12 entries: full

        // The model comes back to page a (get_from_working_memory touches what it reads).
        await memory.TouchAsync("session/s1/web-a-chunk1");
        await Task.Delay(2);

        var write = await memory.TrySetAsync("session/s1/web-d-chunk0", "d part 0", category: "web");

        Assert.IsTrue(write.Stored);
        CollectionAssert.AreEquivalent(PageKeys("b", 3), write.EvictedKeys!.ToArray(),
            "b is the page read least recently; a was just read");
        Assert.AreEqual("a part 0", await memory.GetAsync("session/s1/web-a-chunk0"), "reading one chunk keeps the whole page");
        Assert.AreEqual("c part 2", await memory.GetAsync("session/s1/web-c-chunk2"));
    }

    [TestMethod]
    public async Task ReadingThePageIndex_KeepsThePage()
    {
        var memory = Memory(maxRegenerable: 8);
        await StorePageAsync(memory, "a", chunks: 3);
        await StorePageAsync(memory, "b", chunks: 3); // full

        await memory.TouchAsync("session/s1/web-a-index");
        await Task.Delay(2);

        var write = await memory.TrySetAsync("session/s1/web-c-chunk0", "c part 0", category: "web");

        CollectionAssert.AreEquivalent(PageKeys("b", 3), write.EvictedKeys!.ToArray());
        Assert.AreEqual("a part 2", await memory.GetAsync("session/s1/web-a-chunk2"));
    }

    [TestMethod]
    public async Task BookkeepingReads_DontCountAsModelReads()
    {
        var memory = Memory(maxRegenerable: 8);
        await StorePageAsync(memory, "a", chunks: 3);
        await StorePageAsync(memory, "b", chunks: 3); // full

        // The context builder ranks page indexes with GetAsync and the trimmer checks keys still
        // exist with it; inventories and searches touch everything.
        await memory.GetAsync("session/s1/web-a-index");
        await memory.GetEntryAsync("session/s1/web-a-chunk0");
        await memory.ListAsync("session/s1/");
        await memory.SearchAsync(new MemorySearchCriteria(Query: "part"), "session/s1/");
        await Task.Delay(2);

        var write = await memory.TrySetAsync("session/s1/web-c-chunk0", "c part 0", category: "web");

        CollectionAssert.AreEquivalent(PageKeys("a", 3), write.EvictedKeys!.ToArray(),
            "none of those is the model reading the page, so a is still the oldest");
    }

    [TestMethod]
    public async Task GetFromWorkingMemory_CountsAsARead()
    {
        var memory = Memory(maxRegenerable: 8);
        await StorePageAsync(memory, "a", chunks: 3);
        await StorePageAsync(memory, "b", chunks: 3); // full
        var tools = new WorkingMemoryTools(memory, "session/s1", NullLogger.Instance);

        StringAssert.Contains(await tools.GetFromWorkingMemory("web-a-chunk2"), "a part 2");
        await Task.Delay(2);
        var write = await memory.TrySetAsync("session/s1/web-c-chunk0", "c part 0", category: "web");

        CollectionAssert.AreEquivalent(PageKeys("b", 3), write.EvictedKeys!.ToArray());
    }

    [TestMethod]
    public async Task APage_BiggerThanTheRoom_DoesntEvictItsOwnChunks()
    {
        var memory = Memory(maxRegenerable: 2);
        await memory.SetAsync("session/s1/plan", "my plan");

        var results = new List<WorkingMemoryWriteResult>();
        for (var i = 0; i < 4; i++)
            results.Add(await memory.TrySetAsync($"session/s1/web-big-chunk{i}", $"big {i}", category: "web"));

        Assert.IsTrue(results[0].Stored && results[1].Stored);
        Assert.IsFalse(results[2].Stored, "the page's own chunks are not eviction candidates");
        StringAssert.Contains(results[2].Reason, "full");
        Assert.AreEqual("big 0", await memory.GetAsync("session/s1/web-big-chunk0"));
    }

    [TestMethod]
    public async Task EvictionIsPerNamespace()
    {
        var memory = Memory(maxRegenerable: 2);
        await StorePageAsync(memory, "other", chunks: 1);   // session/s1: full
        await memory.SetAsync("session/s2/web-big-chunk0", "big 0", category: "web");
        await memory.SetAsync("session/s2/web-big-chunk1", "big 1", category: "web");

        var write = await memory.TrySetAsync("session/s2/web-big-chunk2", "big 2", category: "web");

        Assert.IsFalse(write.Stored, "s1's page can't make room in s2");
        Assert.AreEqual("other part 0", await memory.GetAsync("session/s1/web-other-chunk0"));
    }

    [TestMethod]
    public async Task RegenerableCategories_MatchIgnoringCase()
    {
        var memory = Memory(maxEntries: 1);
        await memory.SetAsync("session/s1/plan", "my plan"); // the saved-entry budget is full

        var write = await memory.TrySetAsync("session/s1/web-p-chunk0", "p part 0", category: "Web");

        Assert.IsTrue(write.Stored, "'Web' is page content, so it doesn't need a saved-entry slot");
    }

    // ── The store: saved entries keep their own budget ──────────────────────

    [TestMethod]
    public async Task PageChunks_DontCountAgainstSavedEntries()
    {
        var memory = Memory(maxEntries: 2, maxRegenerable: 8);
        await StorePageAsync(memory, "a", chunks: 3);
        await StorePageAsync(memory, "b", chunks: 3); // the page budget is full

        var plan = await memory.TrySetAsync("session/s1/plan", "my plan");
        var notes = await memory.TrySetAsync("session/s1/notes", "my notes", category: "research");

        Assert.IsTrue(plan.Stored && notes.Stored);
        Assert.IsNull(plan.EvictedKeys);
        Assert.IsNull(notes.EvictedKeys);
        foreach (var key in PageKeys("a", 3).Concat(PageKeys("b", 3)))
            Assert.IsNotNull(await memory.GetAsync(key), $"{key} is still readable");
    }

    [TestMethod]
    public async Task SavedEntriesFull_IsRefused_WithoutEvictingPages()
    {
        var memory = Memory(maxEntries: 3);
        await memory.SetAsync("session/s1/plan", "my plan");
        await memory.SetAsync("session/s1/notes", "my notes", category: "research");
        await StorePageAsync(memory, "page", chunks: 2);

        var findings = await memory.TrySetAsync("session/s1/findings", "findings");
        Assert.IsTrue(findings.Stored);
        Assert.IsNull(findings.EvictedKeys, "the page never took a saved-entry slot");

        var refused = await memory.TrySetAsync("session/s1/more", "more");
        Assert.IsFalse(refused.Stored, "the saved-entry budget is full");
        StringAssert.Contains(refused.Reason, "full");
        Assert.IsNull(await memory.GetAsync("session/s1/more"));
        foreach (var key in PageKeys("page", 2))
            Assert.IsNotNull(await memory.GetAsync(key), "page chunks are never evicted for a saved entry");
        Assert.AreEqual("my plan", await memory.GetAsync("session/s1/plan"));
    }

    [TestMethod]
    public async Task OverwritingASavedEntry_IsAllowedWhenFull()
    {
        var memory = Memory(maxEntries: 1);
        await memory.SetAsync("session/s1/plan", "my plan");

        var write = await memory.TrySetAsync("session/s1/plan", "revised plan");

        Assert.IsTrue(write.Stored);
        Assert.AreEqual("revised plan", await memory.GetAsync("session/s1/plan"));
    }

    [TestMethod]
    public async Task OverwritingAKey_IntoTheOtherBudget_IsAllowedWhenFull()
    {
        var memory = Memory(maxEntries: 1, maxRegenerable: 1);
        await memory.SetAsync("session/s1/plan", "my plan");                                  // saved budget full
        await memory.SetAsync("session/s1/web-x-chunk0", "x part 0", category: "web");        // page budget full

        var annotate = await memory.TrySetAsync("session/s1/web-x-chunk0", "x part 0, annotated"); // page -> saved
        var demote = await memory.TrySetAsync("session/s1/plan", "my plan", category: "web");       // saved -> page

        Assert.IsTrue(annotate.Stored, "an overwrite adds no entry");
        Assert.IsTrue(demote.Stored, "an overwrite adds no entry");
    }

    [TestMethod]
    public async Task ANamespace_IsNotAPrefixOfAnother()
    {
        var memory = Memory(maxEntries: 1);
        await memory.SetAsync("session/s1/plan", "my plan");

        var write = await memory.TrySetAsync("session/s10/plan", "another session's plan");

        Assert.IsTrue(write.Stored, "session/s10 is not in session/s1");
    }

    [TestMethod]
    public async Task NoEvictableCategories_RefusesAsBefore()
    {
        var memory = Memory(maxEntries: 2, evictable: []);
        await StorePageAsync(memory, "page", chunks: 1);

        var write = await memory.TrySetAsync("session/s1/web-next-chunk0", "next", category: "web");

        Assert.IsFalse(write.Stored, "with no regenerable categories, pages are ordinary saved entries");
    }

    [TestMethod]
    public async Task DefaultBudgets_HoldTwelveLongPagesAndTheAgentsNotes()
    {
        var memory = Memory(maxEntries: new WorkingMemoryOptions().MaxEntriesPerNamespace,
            maxRegenerable: new WorkingMemoryOptions().MaxRegenerableEntriesPerNamespace);

        for (var n = 0; n < 20; n++)
            Assert.IsTrue((await memory.TrySetAsync($"session/s1/note{n}", $"note {n}")).Stored);
        for (var p = 0; p < 12; p++)
            await StorePageAsync(memory, $"lang{p}", chunks: 30, pace: false); // 372 entries

        for (var p = 0; p < 12; p++)
            Assert.AreEqual($"lang{p} part 17", await memory.GetAsync($"session/s1/web-lang{p}-chunk17"),
                "a turn that read 12 long pages can still read the first ones");
        for (var n = 0; n < 20; n++)
            Assert.AreEqual($"note {n}", await memory.GetAsync($"session/s1/note{n}"));
    }

    [TestMethod]
    [DataRow("session/s1/web-x-chunk12", "session/s1/web-x")]
    [DataRow("session/s1/tool-search-ab12-index", "session/s1/tool-search-ab12")]
    [DataRow("session/s1/plan", "session/s1/plan")]
    public void ChunkGroup_StripsTheChunkSuffix(string key, string group) =>
        Assert.AreEqual(group, HybridCacheWorkingMemory.ChunkGroup(key));

    // ── save_to_working_memory: reserved categories ─────────────────────────

    [TestMethod]
    public async Task SaveTool_WithACachedContentCategory_StaysASavedEntry()
    {
        var memory = Memory(maxEntries: 1, maxRegenerable: 4);
        var tools = new WorkingMemoryTools(memory, "session/s1", NullLogger.Instance);

        var result = await tools.SaveToWorkingMemory("findings", "what I found", category: "web");

        StringAssert.Contains(result, "web/saved");
        var entry = await memory.GetEntryAsync("session/s1/findings");
        Assert.AreEqual("web/saved", entry!.Category);
        StringAssert.Contains(await tools.SaveToWorkingMemory("more", "more", category: "Web"), "NOT saved",
            "it took the one saved-entry slot rather than escaping the limit");

        // A page filling the page budget can't evict it.
        for (var i = 0; i < 6; i++)
            await memory.SetAsync($"session/s1/web-p{i}-chunk0", $"p{i}", category: "web");
        Assert.AreEqual("what I found", await memory.GetAsync("session/s1/findings"));

        var search = await memory.SearchAsync(new MemorySearchCriteria(Category: "web"), "session/s1/");
        Assert.IsTrue(search.Any(e => e.Key == "session/s1/findings"), "a category search for 'web' still finds it");
    }

    // ── Persistence ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task FileStore_DoesntPersistPageChunks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rockbot-wm-803-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new WorkingMemoryOptions { BasePath = dir });
            var inner = new HybridCacheWorkingMemory(_cache, options, Options.Create(new EmbeddingOptions()),
                EmbeddingTextPreparer.ForTests(), NullLogger<HybridCacheWorkingMemory>.Instance);
            var store = new FileWorkingMemory(inner, options,
                Options.Create(new AgentProfileOptions { BasePath = dir }), NullLogger<FileWorkingMemory>.Instance);
            var file = Path.Combine(dir, "session.json");

            await store.TrySetAsync("session/s1/web-p-chunk0", "p part 0", category: "web");
            Assert.IsFalse(File.Exists(file), "a page chunk alone writes nothing to disk");

            await store.SetAsync("session/s1/plan", "my plan");
            await store.TrySetAsync("session/s1/web-p-chunk1", "p part 1", category: "web");
            var json = await File.ReadAllTextAsync(file);
            StringAssert.Contains(json, "session/s1/plan");
            Assert.IsFalse(json.Contains("web-p-chunk"), "chunks are never persisted");

            // Overwriting the saved entry with a chunk takes it off disk.
            await store.TrySetAsync("session/s1/plan", "now a chunk", category: "web");
            Assert.IsFalse(File.Exists(file) && (await File.ReadAllTextAsync(file)).Contains("session/s1/plan"));
            Assert.AreEqual("p part 1", await store.GetAsync("session/s1/web-p-chunk1"), "still in memory");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Inventory ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Inventory_ListsAChunkedPageAsOneLine()
    {
        var now = DateTimeOffset.UtcNow;
        WorkingMemoryEntry E(string key, string? category = "web") =>
            new(key, "v", now, now.AddMinutes(20), category, []);
        var entries = new List<WorkingMemoryEntry>
        {
            E("session/s1/plan", category: null),
            E("session/s1/web-a-chunk0"), E("session/s1/web-a-chunk1"), E("session/s1/web-a-chunk2"), E("session/s1/web-a-index"),
            E("session/s1/web-b-chunk0"), E("session/s1/web-b-chunk1"),
            E("session/s1/tool-x-chunk0"),
        };

        var lines = AgentContextBuilder.BuildWorkingMemoryInventoryLines(entries, now);

        Assert.AreEqual(4, lines.Count, string.Join("\n", lines));
        StringAssert.StartsWith(lines[0], "- session/s1/plan:");
        StringAssert.Contains(lines[1], "session/s1/web-a-index (outline of 3 chunk(s)");
        StringAssert.Contains(lines[1], "category: web");
        StringAssert.Contains(lines[2], "session/s1/web-b-chunk0… (2 chunk(s), no outline)");
        StringAssert.StartsWith(lines[3], "- session/s1/tool-x-chunk0:", "a lone chunk is listed as itself");
    }

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
        var memory = Memory(maxRegenerable: 2);
        await memory.SetAsync("session/s1/plan", "my plan"); // saved entries don't use the page budget
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

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class HybridCacheSharedMemoryTests
{
    private IMemoryCache _cache = null!;
    private HybridCacheSharedMemory _memory = null!;

    [TestInitialize]
    public void Setup()
    {
        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        _memory = CreateMemory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _cache.Dispose();
    }

    // ── Set / Get round-trip ───────────────────────────────────────────────

    [TestMethod]
    public async Task SetAsync_And_GetAsync_ReturnsValue()
    {
        await _memory.SetAsync("key1", "hello");
        var result = await _memory.GetAsync("key1");
        Assert.AreEqual("hello", result);
    }

    [TestMethod]
    public async Task GetAsync_UnknownKey_ReturnsNull()
    {
        var result = await _memory.GetAsync("missing");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task SetAsync_OverwritesExistingKey()
    {
        await _memory.SetAsync("key1", "first");
        await _memory.SetAsync("key1", "second");
        var result = await _memory.GetAsync("key1");
        Assert.AreEqual("second", result);
    }

    // ── Expiry ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetAsync_ExpiredEntry_ReturnsNull()
    {
        await _memory.SetAsync("key1", "data", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50); // let it expire
        var result = await _memory.GetAsync("key1");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ListAsync_PrunesExpiredEntries()
    {
        await _memory.SetAsync("live", "still here", TimeSpan.FromMinutes(5));
        await _memory.SetAsync("dead", "gone", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        var entries = await _memory.ListAsync();

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("live", entries[0].Key);
    }

    // ── List ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ListAsync_EmptyMemory_ReturnsEmpty()
    {
        var entries = await _memory.ListAsync();
        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task ListAsync_ReturnsAllLiveEntries()
    {
        await _memory.SetAsync("a", "alpha");
        await _memory.SetAsync("b", "beta");
        await _memory.SetAsync("c", "gamma");

        var entries = await _memory.ListAsync();

        Assert.AreEqual(3, entries.Count);
        var keys = entries.Select(e => e.Key).ToHashSet();
        Assert.IsTrue(keys.Contains("a"));
        Assert.IsTrue(keys.Contains("b"));
        Assert.IsTrue(keys.Contains("c"));
    }

    [TestMethod]
    public async Task ListAsync_EntryHasCorrectMetadata()
    {
        var before = DateTimeOffset.UtcNow;
        await _memory.SetAsync("key1", "value1", TimeSpan.FromMinutes(10));
        var after = DateTimeOffset.UtcNow;

        var entries = await _memory.ListAsync();

        Assert.AreEqual(1, entries.Count);
        var entry = entries[0];
        Assert.AreEqual("key1", entry.Key);
        Assert.AreEqual("value1", entry.Value);
        Assert.IsTrue(entry.StoredAt >= before && entry.StoredAt <= after);
        Assert.IsTrue(entry.ExpiresAt > DateTimeOffset.UtcNow);
    }

    // ── Delete ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task DeleteAsync_RemovesEntry()
    {
        await _memory.SetAsync("key1", "data");
        await _memory.DeleteAsync("key1");

        var result = await _memory.GetAsync("key1");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DeleteAsync_NonexistentKey_NoOp()
    {
        // Should not throw
        await _memory.DeleteAsync("phantom");
    }

    [TestMethod]
    public async Task DeleteAsync_DoesNotAffectOtherKeys()
    {
        await _memory.SetAsync("keep", "safe");
        await _memory.SetAsync("remove", "gone");
        await _memory.DeleteAsync("remove");

        Assert.IsNull(await _memory.GetAsync("remove"));
        Assert.AreEqual("safe", await _memory.GetAsync("keep"));
    }

    // ── Clear ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ClearAsync_RemovesAllEntries()
    {
        await _memory.SetAsync("a", "1");
        await _memory.SetAsync("b", "2");
        await _memory.ClearAsync();

        var entries = await _memory.ListAsync();
        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task ClearAsync_EmptyMemory_NoOp()
    {
        // Should not throw
        await _memory.ClearAsync();
    }

    // ── MaxEntries ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task MaxEntries_RejectsNewKeys_WhenLimitReached()
    {
        _memory = CreateMemory(maxEntries: 2);

        await _memory.SetAsync("a", "1");
        await _memory.SetAsync("b", "2");
        await _memory.SetAsync("c", "SHOULD NOT BE STORED");

        var entries = await _memory.ListAsync();
        Assert.AreEqual(2, entries.Count);
        Assert.IsNull(await _memory.GetAsync("c"));
    }

    [TestMethod]
    public async Task MaxEntries_AllowsOverwriteOfExistingKey()
    {
        _memory = CreateMemory(maxEntries: 2);

        await _memory.SetAsync("a", "original");
        await _memory.SetAsync("b", "b-value");
        await _memory.SetAsync("a", "updated"); // overwrite — should succeed

        Assert.AreEqual("updated", await _memory.GetAsync("a"));
        var entries = await _memory.ListAsync();
        Assert.AreEqual(2, entries.Count);
    }

    // ── DI registration ────────────────────────────────────────────────────

    [TestMethod]
    public void WithSharedMemory_RegistersISharedMemory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithSharedMemory();
        });

        var provider = services.BuildServiceProvider();
        var sm = provider.GetService<ISharedMemory>();

        Assert.IsNotNull(sm);
    }

    [TestMethod]
    public void WithSharedMemory_CustomOptions_Configures()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithSharedMemory(o =>
            {
                o.DefaultTtl = TimeSpan.FromMinutes(60);
                o.MaxEntries = 50;
            });
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SharedMemoryOptions>>();

        Assert.AreEqual(TimeSpan.FromMinutes(60), options.Value.DefaultTtl);
        Assert.AreEqual(50, options.Value.MaxEntries);
    }

    [TestMethod]
    public void WithSharedMemory_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithSharedMemory();
        });

        var provider = services.BuildServiceProvider();
        var sm1 = provider.GetRequiredService<ISharedMemory>();
        var sm2 = provider.GetRequiredService<ISharedMemory>();

        Assert.AreSame(sm1, sm2);
    }

    [TestMethod]
    public void WithMemory_IncludesSharedMemory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithProfile();
            agent.WithMemory();
        });

        var provider = services.BuildServiceProvider();

        Assert.IsNotNull(provider.GetService<ISharedMemory>());
    }

    // ── Search — BM25 ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task SearchAsync_TermInValue_ReturnsMatch()
    {
        await _memory.SetAsync("doc-a", "The quick brown fox");
        await _memory.SetAsync("doc-b", "A slow blue turtle");

        var results = await _memory.SearchAsync(new MemorySearchCriteria(Query: "quick"));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("doc-a", results[0].Key);
    }

    [TestMethod]
    public async Task SearchAsync_TermInKey_ReturnsMatch()
    {
        await _memory.SetAsync("calendar_2026-03", "March events");
        await _memory.SetAsync("emails_inbox", "50 unread");

        var results = await _memory.SearchAsync(new MemorySearchCriteria(Query: "calendar"));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("calendar_2026-03", results[0].Key);
    }

    [TestMethod]
    public async Task SearchAsync_TermInTag_ReturnsMatch()
    {
        await _memory.SetAsync("doc-a", "Some data", tags: ["urgent", "finance"]);
        await _memory.SetAsync("doc-b", "Other data", tags: ["routine"]);

        var results = await _memory.SearchAsync(new MemorySearchCriteria(Query: "urgent"));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("doc-a", results[0].Key);
    }

    [TestMethod]
    public async Task SearchAsync_TermInCategory_ReturnsMatch()
    {
        await _memory.SetAsync("doc-a", "Negotiation details", category: "research/pricing");
        await _memory.SetAsync("doc-b", "Meeting notes", category: "calendar");

        var results = await _memory.SearchAsync(new MemorySearchCriteria(Query: "pricing"));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("doc-a", results[0].Key);
    }

    [TestMethod]
    public async Task SearchAsync_CaseInsensitive()
    {
        await _memory.SetAsync("k", "Hello World");

        var results = await _memory.SearchAsync(new MemorySearchCriteria(Query: "hello world"));

        Assert.AreEqual(1, results.Count);
    }

    [TestMethod]
    public async Task SearchAsync_NoMatch_ReturnsEmpty()
    {
        await _memory.SetAsync("k", "some content here");

        var results = await _memory.SearchAsync(new MemorySearchCriteria(Query: "zxyzxyzxy"));

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task SearchAsync_NullQuery_ReturnsAllEntriesOrderedByRecency()
    {
        await _memory.SetAsync("a", "alpha");
        await _memory.SetAsync("b", "beta");

        var results = await _memory.SearchAsync(new MemorySearchCriteria());

        Assert.AreEqual(2, results.Count);
    }

    [TestMethod]
    public async Task SearchAsync_DoesNotReturnExpiredEntries()
    {
        await _memory.SetAsync("live", "quick fox", TimeSpan.FromMinutes(5));
        await _memory.SetAsync("dead", "quick fox", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        var results = await _memory.SearchAsync(new MemorySearchCriteria(Query: "quick"));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("live", results[0].Key);
    }

    // ── Search — category filter ────────────────────────────────────────────

    [TestMethod]
    public async Task SearchAsync_CategoryFilter_ExcludesOtherCategories()
    {
        await _memory.SetAsync("a", "pricing data", category: "research/pricing");
        await _memory.SetAsync("b", "pricing data", category: "calendar");

        var results = await _memory.SearchAsync(new MemorySearchCriteria(Query: "pricing", Category: "research"));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("a", results[0].Key);
    }

    [TestMethod]
    public async Task SearchAsync_CategoryFilter_PrefixMatch()
    {
        await _memory.SetAsync("a", "data", category: "research/pricing");
        await _memory.SetAsync("b", "data", category: "research/competitors");
        await _memory.SetAsync("c", "data", category: "calendar");

        var results = await _memory.SearchAsync(new MemorySearchCriteria(Category: "research"));

        Assert.AreEqual(2, results.Count);
        var keys = results.Select(e => e.Key).ToHashSet();
        Assert.IsTrue(keys.Contains("a"));
        Assert.IsTrue(keys.Contains("b"));
    }

    [TestMethod]
    public async Task SearchAsync_CategoryFilter_NoQuery_ReturnsMatchingEntries()
    {
        await _memory.SetAsync("a", "stuff", category: "email");
        await _memory.SetAsync("b", "stuff", category: "calendar");

        var results = await _memory.SearchAsync(new MemorySearchCriteria(Category: "email"));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("a", results[0].Key);
    }

    // ── Search — tag filter ─────────────────────────────────────────────────

    [TestMethod]
    public async Task SearchAsync_TagFilter_RequiresAllTags()
    {
        await _memory.SetAsync("a", "data", tags: ["urgent", "finance"]);
        await _memory.SetAsync("b", "data", tags: ["urgent"]);
        await _memory.SetAsync("c", "data", tags: ["finance"]);

        var results = await _memory.SearchAsync(
            new MemorySearchCriteria(Tags: ["urgent", "finance"]));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("a", results[0].Key);
    }

    [TestMethod]
    public async Task SearchAsync_TagFilter_WithQuery_CombinesFilters()
    {
        await _memory.SetAsync("a", "pricing strategy doc", tags: ["urgent"]);
        await _memory.SetAsync("b", "pricing strategy doc");  // no tags

        var results = await _memory.SearchAsync(
            new MemorySearchCriteria(Query: "pricing", Tags: ["urgent"]));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("a", results[0].Key);
    }

    // ── Search — metadata on entries ────────────────────────────────────────

    [TestMethod]
    public async Task SetAsync_WithCategoryAndTags_PersistedInEntry()
    {
        await _memory.SetAsync("key1", "value",
            category: "research/pricing",
            tags: ["urgent", "finance"]);

        var entries = await _memory.ListAsync();

        Assert.AreEqual(1, entries.Count);
        var e = entries[0];
        Assert.AreEqual("research/pricing", e.Category);
        Assert.AreEqual(2, e.Tags!.Count);
        Assert.IsTrue(e.Tags.Contains("urgent"));
        Assert.IsTrue(e.Tags.Contains("finance"));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private HybridCacheSharedMemory CreateMemory(int maxEntries = 100, TimeSpan? defaultTtl = null)
    {
        var opts = new SharedMemoryOptions
        {
            DefaultTtl = defaultTtl ?? TimeSpan.FromMinutes(30),
            MaxEntries = maxEntries
        };
        return new HybridCacheSharedMemory(
            _cache,
            Options.Create(opts),
            NullLogger<HybridCacheSharedMemory>.Instance);
    }
}

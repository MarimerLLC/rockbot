using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class HybridCacheWorkingMemoryTests
{
    private IMemoryCache _cache = null!;
    private HybridCacheWorkingMemory _memory = null!;

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
        await _memory.SetAsync("s1", "key1", "hello");
        var result = await _memory.GetAsync("s1", "key1");
        Assert.AreEqual("hello", result);
    }

    [TestMethod]
    public async Task GetAsync_UnknownKey_ReturnsNull()
    {
        var result = await _memory.GetAsync("s1", "missing");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetAsync_UnknownSession_ReturnsNull()
    {
        var result = await _memory.GetAsync("no-such-session", "key");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task SetAsync_OverwritesExistingKey()
    {
        await _memory.SetAsync("s1", "key1", "first");
        await _memory.SetAsync("s1", "key1", "second");
        var result = await _memory.GetAsync("s1", "key1");
        Assert.AreEqual("second", result);
    }

    // ── Expiry ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetAsync_ExpiredEntry_ReturnsNull()
    {
        await _memory.SetAsync("s1", "key1", "data", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50); // let it expire
        var result = await _memory.GetAsync("s1", "key1");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ListAsync_PrunesExpiredEntries()
    {
        await _memory.SetAsync("s1", "live", "still here", TimeSpan.FromMinutes(5));
        await _memory.SetAsync("s1", "dead", "gone", TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        var entries = await _memory.ListAsync("s1");

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("live", entries[0].Key);
    }

    // ── List ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ListAsync_UnknownSession_ReturnsEmpty()
    {
        var entries = await _memory.ListAsync("no-such-session");
        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task ListAsync_ReturnsAllLiveEntries()
    {
        await _memory.SetAsync("s1", "a", "alpha");
        await _memory.SetAsync("s1", "b", "beta");
        await _memory.SetAsync("s1", "c", "gamma");

        var entries = await _memory.ListAsync("s1");

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
        await _memory.SetAsync("s1", "key1", "value1", TimeSpan.FromMinutes(10));
        var after = DateTimeOffset.UtcNow;

        var entries = await _memory.ListAsync("s1");

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
        await _memory.SetAsync("s1", "key1", "data");
        await _memory.DeleteAsync("s1", "key1");

        var result = await _memory.GetAsync("s1", "key1");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DeleteAsync_NonexistentKey_NoOp()
    {
        // Should not throw
        await _memory.DeleteAsync("s1", "phantom");
    }

    [TestMethod]
    public async Task DeleteAsync_DoesNotAffectOtherKeys()
    {
        await _memory.SetAsync("s1", "keep", "safe");
        await _memory.SetAsync("s1", "remove", "gone");
        await _memory.DeleteAsync("s1", "remove");

        Assert.IsNull(await _memory.GetAsync("s1", "remove"));
        Assert.AreEqual("safe", await _memory.GetAsync("s1", "keep"));
    }

    // ── Clear ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ClearAsync_RemovesAllSessionEntries()
    {
        await _memory.SetAsync("s1", "a", "1");
        await _memory.SetAsync("s1", "b", "2");
        await _memory.ClearAsync("s1");

        var entries = await _memory.ListAsync("s1");
        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task ClearAsync_NonexistentSession_NoOp()
    {
        // Should not throw
        await _memory.ClearAsync("ghost");
    }

    // ── Session isolation ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Sessions_AreIsolated()
    {
        await _memory.SetAsync("s1", "key", "session-one");
        await _memory.SetAsync("s2", "key", "session-two");

        Assert.AreEqual("session-one", await _memory.GetAsync("s1", "key"));
        Assert.AreEqual("session-two", await _memory.GetAsync("s2", "key"));
    }

    [TestMethod]
    public async Task ClearAsync_DoesNotAffectOtherSessions()
    {
        await _memory.SetAsync("s1", "key", "value");
        await _memory.SetAsync("s2", "key", "other");

        await _memory.ClearAsync("s1");

        Assert.IsNull(await _memory.GetAsync("s1", "key"));
        Assert.AreEqual("other", await _memory.GetAsync("s2", "key"));
    }

    // ── MaxEntriesPerSession ───────────────────────────────────────────────

    [TestMethod]
    public async Task MaxEntriesPerSession_RejectsNewKeys_WhenLimitReached()
    {
        _memory = CreateMemory(maxEntries: 2);

        await _memory.SetAsync("s1", "a", "1");
        await _memory.SetAsync("s1", "b", "2");
        await _memory.SetAsync("s1", "c", "SHOULD NOT BE STORED");

        var entries = await _memory.ListAsync("s1");
        Assert.AreEqual(2, entries.Count);
        Assert.IsNull(await _memory.GetAsync("s1", "c"));
    }

    [TestMethod]
    public async Task MaxEntriesPerSession_AllowsOverwriteOfExistingKey()
    {
        _memory = CreateMemory(maxEntries: 2);

        await _memory.SetAsync("s1", "a", "original");
        await _memory.SetAsync("s1", "b", "b-value");
        await _memory.SetAsync("s1", "a", "updated"); // overwrite — should succeed

        Assert.AreEqual("updated", await _memory.GetAsync("s1", "a"));
        var entries = await _memory.ListAsync("s1");
        Assert.AreEqual(2, entries.Count);
    }

    // ── DI registration ────────────────────────────────────────────────────

    [TestMethod]
    public void WithWorkingMemory_RegistersIWorkingMemory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithWorkingMemory();
        });

        var provider = services.BuildServiceProvider();
        var wm = provider.GetService<IWorkingMemory>();

        Assert.IsNotNull(wm);
    }

    [TestMethod]
    public void WithWorkingMemory_CustomOptions_Configures()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithWorkingMemory(o =>
            {
                o.DefaultTtl = TimeSpan.FromMinutes(10);
                o.MaxEntriesPerSession = 5;
            });
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WorkingMemoryOptions>>();

        Assert.AreEqual(TimeSpan.FromMinutes(10), options.Value.DefaultTtl);
        Assert.AreEqual(5, options.Value.MaxEntriesPerSession);
    }

    [TestMethod]
    public void WithWorkingMemory_IsSingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRockBotHost(agent =>
        {
            agent.WithIdentity("test-agent");
            agent.WithWorkingMemory();
        });

        var provider = services.BuildServiceProvider();
        var wm1 = provider.GetRequiredService<IWorkingMemory>();
        var wm2 = provider.GetRequiredService<IWorkingMemory>();

        Assert.AreSame(wm1, wm2);
    }

    [TestMethod]
    public void WithMemory_IncludesWorkingMemory()
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

        Assert.IsNotNull(provider.GetService<IWorkingMemory>());
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private HybridCacheWorkingMemory CreateMemory(int maxEntries = 20, TimeSpan? defaultTtl = null)
    {
        var opts = new WorkingMemoryOptions
        {
            DefaultTtl = defaultTtl ?? TimeSpan.FromMinutes(5),
            MaxEntriesPerSession = maxEntries
        };
        return new HybridCacheWorkingMemory(
            _cache,
            Options.Create(opts),
            NullLogger<HybridCacheWorkingMemory>.Instance);
    }
}

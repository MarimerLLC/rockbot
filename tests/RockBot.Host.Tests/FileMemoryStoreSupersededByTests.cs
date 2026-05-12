using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Phase 3 self-repair: confirms that <see cref="MemoryEntry.SupersededBy"/> entries
/// are hidden from <see cref="ILongTermMemory.SearchAsync"/> by default but remain
/// retrievable by <see cref="ILongTermMemory.GetAsync"/> for audit and supersession
/// traversal. Round-trip serialisation is also verified.
/// </summary>
[TestClass]
public class FileMemoryStoreSupersededByTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-superseded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task SearchAsync_HidesSupersededEntriesByDefault()
    {
        var store = NewStore();
        var winner = NewEntry("w-1", "wrapper does pass arguments", "claim/capability/calendar-mcp/get_calendar_events");
        var loser = NewEntry("l-1", "wrapper cannot pass arguments", "claim/capability/calendar-mcp/get_calendar_events")
            with
        { SupersededBy = "w-1" };

        await store.SaveAsync(winner);
        await store.SaveAsync(loser);

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Category: "claim/capability", MaxResults: 50));

        Assert.AreEqual(1, results.Count, "Superseded entry should not appear in SearchAsync results.");
        Assert.AreEqual("w-1", results[0].Id);
    }

    [TestMethod]
    public async Task SearchAsync_IncludeSuperseded_ReturnsAllEntries()
    {
        var store = NewStore();
        await store.SaveAsync(NewEntry("w-1", "wrapper does pass arguments", "claim/capability/x/y"));
        await store.SaveAsync(NewEntry("l-1", "wrapper cannot pass arguments", "claim/capability/x/y")
            with
        { SupersededBy = "w-1" });

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Category: "claim/capability", MaxResults: 50, IncludeSuperseded: true));

        Assert.AreEqual(2, results.Count);
    }

    [TestMethod]
    public async Task GetAsync_ReturnsSupersededEntryByIdForAudit()
    {
        var store = NewStore();
        var loser = NewEntry("l-1", "wrapper cannot pass arguments", "claim/capability/x/y")
            with
        { SupersededBy = "w-1" };
        await store.SaveAsync(loser);

        var fetched = await store.GetAsync("l-1");

        Assert.IsNotNull(fetched);
        Assert.AreEqual("w-1", fetched!.SupersededBy);
    }

    [TestMethod]
    public async Task SaveAsync_RoundTripsSupersededByThroughDisk()
    {
        var saved = NewEntry("l-1", "old claim", "claim/capability/x/y") with { SupersededBy = "winner" };

        // First store writes the entry; second store reads from disk via index.
        var basePath = Path.Combine(_tempDir, "ltm");
        Directory.CreateDirectory(basePath);

        var store1 = NewStore(basePath);
        await store1.SaveAsync(saved);

        var store2 = NewStore(basePath);
        var loaded = await store2.GetAsync("l-1");

        Assert.IsNotNull(loaded);
        Assert.AreEqual("winner", loaded!.SupersededBy,
            "SupersededBy must round-trip through JSON serialisation.");
    }

    private FileMemoryStore NewStore(string? basePath = null)
    {
        var ltmPath = basePath ?? Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        var memOpts = Options.Create(new MemoryOptions { BasePath = ltmPath });
        var profOpts = Options.Create(new AgentProfileOptions { BasePath = _tempDir });
        var embedOpts = Options.Create(new EmbeddingOptions());
        return new FileMemoryStore(memOpts, profOpts, embedOpts, NullLogger<FileMemoryStore>.Instance, EmbeddingTextPreparer.ForTests());
    }

    private static MemoryEntry NewEntry(string id, string content, string category) =>
        new(
            Id: id,
            Content: content,
            Category: category,
            Tags: [],
            CreatedAt: DateTimeOffset.UtcNow);
}

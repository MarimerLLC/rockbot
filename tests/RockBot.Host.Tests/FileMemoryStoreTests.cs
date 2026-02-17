using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileMemoryStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-memory-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task SaveAsync_And_GetAsync_RoundTrips()
    {
        var store = CreateStore();
        var entry = CreateEntry("test-1", "Important fact", category: null, tags: ["fact"]);

        await store.SaveAsync(entry);
        var result = await store.GetAsync("test-1");

        Assert.IsNotNull(result);
        Assert.AreEqual("test-1", result.Id);
        Assert.AreEqual("Important fact", result.Content);
        Assert.IsNull(result.Category);
        Assert.AreEqual(1, result.Tags.Count);
        Assert.AreEqual("fact", result.Tags[0]);
    }

    [TestMethod]
    public async Task SaveAsync_WithCategory_CreatesSubdirectory()
    {
        var store = CreateStore();
        var entry = CreateEntry("test-1", "User likes dark mode", category: "user-preferences");

        await store.SaveAsync(entry);

        var filePath = Path.Combine(_tempDir, "user-preferences", "test-1.json");
        Assert.IsTrue(File.Exists(filePath));
    }

    [TestMethod]
    public async Task SaveAsync_WithNestedCategory_CreatesNestedSubdirectories()
    {
        var store = CreateStore();
        var entry = CreateEntry("test-1", "RockBot architecture notes", category: "project-context/rockbot");

        await store.SaveAsync(entry);

        var filePath = Path.Combine(_tempDir, "project-context", "rockbot", "test-1.json");
        Assert.IsTrue(File.Exists(filePath));
    }

    [TestMethod]
    public async Task SaveAsync_WithoutCategory_SavesInRoot()
    {
        var store = CreateStore();
        var entry = CreateEntry("test-1", "Uncategorized note");

        await store.SaveAsync(entry);

        var filePath = Path.Combine(_tempDir, "test-1.json");
        Assert.IsTrue(File.Exists(filePath));
    }

    [TestMethod]
    public async Task SaveAsync_OverwritesExistingEntry()
    {
        var store = CreateStore();
        var original = CreateEntry("test-1", "Original content");
        var updated = CreateEntry("test-1", "Updated content");

        await store.SaveAsync(original);
        await store.SaveAsync(updated);

        var result = await store.GetAsync("test-1");
        Assert.IsNotNull(result);
        Assert.AreEqual("Updated content", result.Content);
    }

    [TestMethod]
    public async Task SaveAsync_CategoryChange_RemovesOldFile()
    {
        var store = CreateStore();
        var original = CreateEntry("test-1", "Content", category: "old-category");
        var updated = CreateEntry("test-1", "Content", category: "new-category");

        await store.SaveAsync(original);
        await store.SaveAsync(updated);

        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "old-category", "test-1.json")));
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "new-category", "test-1.json")));
    }

    [TestMethod]
    public async Task GetAsync_NonexistentId_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.GetAsync("nonexistent");

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesEntry()
    {
        var store = CreateStore();
        var entry = CreateEntry("test-1", "To be deleted");

        await store.SaveAsync(entry);
        await store.DeleteAsync("test-1");

        var result = await store.GetAsync("test-1");
        Assert.IsNull(result);
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "test-1.json")));
    }

    [TestMethod]
    public async Task DeleteAsync_NonexistentId_NoOp()
    {
        var store = CreateStore();
        // Should not throw
        await store.DeleteAsync("nonexistent");
    }

    [TestMethod]
    public async Task SearchAsync_ByQuery_CaseInsensitive()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("1", "The sky is blue"));
        await store.SaveAsync(CreateEntry("2", "Grass is green"));
        await store.SaveAsync(CreateEntry("3", "The BLUE whale is huge"));

        var results = await store.SearchAsync(new MemorySearchCriteria(Query: "blue"));

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Any(r => r.Id == "1"));
        Assert.IsTrue(results.Any(r => r.Id == "3"));
    }

    [TestMethod]
    public async Task SearchAsync_ByTags_MatchesAll()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("1", "Entry 1", tags: ["a", "b"]));
        await store.SaveAsync(CreateEntry("2", "Entry 2", tags: ["a"]));
        await store.SaveAsync(CreateEntry("3", "Entry 3", tags: ["a", "b", "c"]));

        var results = await store.SearchAsync(new MemorySearchCriteria(Tags: ["a", "b"]));

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Any(r => r.Id == "1"));
        Assert.IsTrue(results.Any(r => r.Id == "3"));
    }

    [TestMethod]
    public async Task SearchAsync_ByCategoryPrefix()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("1", "Note 1", category: "project-context"));
        await store.SaveAsync(CreateEntry("2", "Note 2", category: "project-context/rockbot"));
        await store.SaveAsync(CreateEntry("3", "Note 3", category: "user-preferences"));

        var results = await store.SearchAsync(new MemorySearchCriteria(Category: "project-context"));

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Any(r => r.Id == "1"));
        Assert.IsTrue(results.Any(r => r.Id == "2"));
    }

    [TestMethod]
    public async Task SearchAsync_ByDateRange()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        await store.SaveAsync(new MemoryEntry("1", "Old", null, [], now.AddDays(-10)));
        await store.SaveAsync(new MemoryEntry("2", "Recent", null, [], now.AddDays(-1)));
        await store.SaveAsync(new MemoryEntry("3", "Today", null, [], now));

        var results = await store.SearchAsync(new MemorySearchCriteria(
            CreatedAfter: now.AddDays(-2)));

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Any(r => r.Id == "2"));
        Assert.IsTrue(results.Any(r => r.Id == "3"));
    }

    [TestMethod]
    public async Task SearchAsync_MaxResults_LimitsOutput()
    {
        var store = CreateStore();
        for (int i = 0; i < 10; i++)
            await store.SaveAsync(CreateEntry($"entry-{i}", $"Content {i}"));

        var results = await store.SearchAsync(new MemorySearchCriteria(MaxResults: 3));

        Assert.AreEqual(3, results.Count);
    }

    [TestMethod]
    public async Task SearchAsync_NullCategoryEntry_ExcludedByCategoryFilter()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("1", "Uncategorized"));
        await store.SaveAsync(CreateEntry("2", "Categorized", category: "notes"));

        var results = await store.SearchAsync(new MemorySearchCriteria(Category: "notes"));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("2", results[0].Id);
    }

    [TestMethod]
    public async Task ListTagsAsync_ReturnsDistinctSorted()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("1", "E1", tags: ["zebra", "apple"]));
        await store.SaveAsync(CreateEntry("2", "E2", tags: ["apple", "banana"]));

        var tags = await store.ListTagsAsync();

        Assert.AreEqual(3, tags.Count);
        Assert.AreEqual("apple", tags[0]);
        Assert.AreEqual("banana", tags[1]);
        Assert.AreEqual("zebra", tags[2]);
    }

    [TestMethod]
    public async Task ListCategoriesAsync_ReturnsDistinctSorted()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("1", "E1", category: "user-preferences"));
        await store.SaveAsync(CreateEntry("2", "E2", category: "project-context"));
        await store.SaveAsync(CreateEntry("3", "E3")); // no category

        var categories = await store.ListCategoriesAsync();

        Assert.AreEqual(2, categories.Count);
        Assert.AreEqual("project-context", categories[0]);
        Assert.AreEqual("user-preferences", categories[1]);
    }

    [TestMethod]
    public async Task DirectoryAutoCreated_OnFirstSave()
    {
        Assert.IsFalse(Directory.Exists(_tempDir));

        var store = CreateStore();
        await store.SaveAsync(CreateEntry("test-1", "First entry"));

        Assert.IsTrue(Directory.Exists(_tempDir));
    }

    [TestMethod]
    public async Task MalformedJsonFile_SkippedInSearch()
    {
        // Pre-create directory with a bad JSON file
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "bad.json"), "not valid json {{{");

        var store = CreateStore();
        await store.SaveAsync(CreateEntry("good", "Good entry"));

        var results = await store.SearchAsync(new MemorySearchCriteria());

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("good", results[0].Id);
    }

    [TestMethod]
    public void ValidateCategory_RejectsTraversalAttack()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileMemoryStore.ValidateCategory("../../../etc/passwd"));
    }

    [TestMethod]
    public void ValidateCategory_RejectsAbsolutePath()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileMemoryStore.ValidateCategory("/etc/passwd"));
    }

    [TestMethod]
    public void ValidateCategory_RejectsInvalidCharacters()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileMemoryStore.ValidateCategory("some category!@#"));
    }

    [TestMethod]
    public void ValidateCategory_RejectsEmptyString()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            FileMemoryStore.ValidateCategory(""));
    }

    [TestMethod]
    public void ValidateCategory_AcceptsNull()
    {
        // Should not throw
        FileMemoryStore.ValidateCategory(null);
    }

    [TestMethod]
    public void ValidateCategory_AcceptsValidPaths()
    {
        // Should not throw
        FileMemoryStore.ValidateCategory("user-preferences");
        FileMemoryStore.ValidateCategory("project-context/rockbot");
        FileMemoryStore.ValidateCategory("A_B/c-d/E123");
    }

    [TestMethod]
    public void ResolvePath_AbsoluteMemoryPath_UsedDirectly()
    {
        var result = FileMemoryStore.ResolvePath("/data/memory", "agent");
        Assert.AreEqual("/data/memory", result);
    }

    [TestMethod]
    public void ResolvePath_AbsoluteProfilePath_CombinesWithMemory()
    {
        var result = FileMemoryStore.ResolvePath("memory", "/data/agent");
        Assert.AreEqual(Path.Combine("/data/agent", "memory"), result);
    }

    [TestMethod]
    public void ResolvePath_BothRelative_CombinesWithBaseDirectory()
    {
        var result = FileMemoryStore.ResolvePath("memory", "agent");
        var expected = Path.Combine(AppContext.BaseDirectory, "agent", "memory");
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public async Task Index_LoadedFromDisk_OnFirstAccess()
    {
        // Create a store, save an entry, then create a new store instance
        // to verify it loads from disk
        var store1 = CreateStore();
        await store1.SaveAsync(CreateEntry("persisted", "I survive restarts"));

        var store2 = CreateStore();
        var result = await store2.GetAsync("persisted");

        Assert.IsNotNull(result);
        Assert.AreEqual("I survive restarts", result.Content);
    }

    private FileMemoryStore CreateStore()
    {
        return new FileMemoryStore(
            Options.Create(new MemoryOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            NullLogger<FileMemoryStore>.Instance);
    }

    private static MemoryEntry CreateEntry(
        string id,
        string content,
        string? category = null,
        string[]? tags = null)
    {
        return new MemoryEntry(
            id,
            content,
            category,
            tags ?? [],
            DateTimeOffset.UtcNow);
    }
}

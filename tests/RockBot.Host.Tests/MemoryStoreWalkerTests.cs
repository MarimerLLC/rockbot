using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

/// <summary>
/// The walker is what makes the audit read-only. Every one of these cases is something
/// <c>FileMemoryStore</c> would hide or destroy — archived entries it filters out of search,
/// empty category directories it prunes on load, malformed files it skips silently.
/// </summary>
[TestClass]
public class MemoryStoreWalkerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "rockbot-audit-walk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task FindsEntriesInNestedCategoryDirectories()
    {
        Write("a", new MemoryEntry("a", "root fact", null, [], DateTimeOffset.UtcNow));
        Write("user/prefs/b", new MemoryEntry("b", "nested fact", "user/prefs", [], DateTimeOffset.UtcNow));

        var result = await MemoryStoreWalker.WalkAsync(_root, NullLogger.Instance);

        CollectionAssert.AreEquivalent(
            new[] { "a", "b" }, result.Entries.Select(e => e.Id).ToArray());
        Assert.AreEqual(0, result.MalformedFiles);
    }

    [TestMethod]
    public async Task ReturnsArchivedEntriesAlongsideLiveOnes()
    {
        Write("live", new MemoryEntry("live", "kept", null, [], DateTimeOffset.UtcNow));
        Write("gone", new MemoryEntry("gone", "retired", null, [], DateTimeOffset.UtcNow)
        {
            ArchivedAt = DateTimeOffset.UtcNow.AddDays(-3),
            ArchiveReason = "merged into live"
        });

        var result = await MemoryStoreWalker.WalkAsync(_root, NullLogger.Instance);

        Assert.AreEqual(2, result.Entries.Count);
        Assert.AreEqual(1, result.Entries.Count(e => e.ArchivedAt is not null));
    }

    [TestMethod]
    public async Task SkipsMalformedFilesAndCountsThem()
    {
        Write("good", new MemoryEntry("good", "fine", null, [], DateTimeOffset.UtcNow));
        File.WriteAllText(Path.Combine(_root, "broken.json"), "{ not json at all");

        var result = await MemoryStoreWalker.WalkAsync(_root, NullLogger.Instance);

        Assert.AreEqual(1, result.Entries.Count);
        Assert.AreEqual(1, result.MalformedFiles);
    }

    [TestMethod]
    public async Task IgnoresTheEmbeddingCacheDirectory()
    {
        Write("a", new MemoryEntry("a", "fact", null, [], DateTimeOffset.UtcNow));

        var embeddings = Path.Combine(_root, ".embeddings");
        Directory.CreateDirectory(embeddings);
        File.WriteAllText(Path.Combine(embeddings, "manifest.json"), "{\"anything\":1}");
        File.WriteAllBytes(Path.Combine(embeddings, "a.bin"), [1, 2, 3]);

        var result = await MemoryStoreWalker.WalkAsync(_root, NullLogger.Instance);

        Assert.AreEqual(1, result.Entries.Count);
        Assert.AreEqual(0, result.MalformedFiles, "The embedding manifest is not a malformed entry.");
        Assert.AreEqual(0, result.EmptyCategoryDirs, "The embedding folder is not a category.");
    }

    [TestMethod]
    public async Task CountsEmptyCategoryDirectories()
    {
        Write("user/a", new MemoryEntry("a", "fact", "user", [], DateTimeOffset.UtcNow));
        Directory.CreateDirectory(Path.Combine(_root, "abandoned"));
        Directory.CreateDirectory(Path.Combine(_root, "also/abandoned"));

        var result = await MemoryStoreWalker.WalkAsync(_root, NullLogger.Instance);

        // "abandoned", "also" and "also/abandoned" — a parent whose only content is an empty
        // child is itself empty.
        Assert.AreEqual(3, result.EmptyCategoryDirs);
    }

    [TestMethod]
    public async Task DoesNotCountAParentThatHoldsAPopulatedChild()
    {
        Write("user/prefs/a", new MemoryEntry("a", "fact", "user/prefs", [], DateTimeOffset.UtcNow));

        var result = await MemoryStoreWalker.WalkAsync(_root, NullLogger.Instance);

        Assert.AreEqual(0, result.EmptyCategoryDirs);
    }

    [TestMethod]
    public async Task AMissingRootIsAnEmptyCorpus_NotAnError()
    {
        var result = await MemoryStoreWalker.WalkAsync(
            Path.Combine(_root, "does-not-exist"), NullLogger.Instance);

        Assert.AreEqual(0, result.Entries.Count);
        Assert.AreEqual(0, result.EmptyCategoryDirs);
    }

    private void Write(string relativePath, MemoryEntry entry)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entry, JsonOptions));
    }
}

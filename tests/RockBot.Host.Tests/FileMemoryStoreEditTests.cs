using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers <see cref="FileMemoryStore.EditAsync"/> — the partial-edit path that exists so a
/// correction does not cost an entry its identity and history.
/// </summary>
[TestClass]
public sealed class FileMemoryStoreEditTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-memory-edit-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Success ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditAsync_ReplacesMatchedText()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "User prefers meetings in the morning"));

        var result = await store.EditAsync("m1", "in the morning", "after 13:00");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.ReplacementCount);
        var updated = await store.GetAsync("m1");
        Assert.AreEqual("User prefers meetings after 13:00", updated!.Content);
    }

    [TestMethod]
    public async Task EditAsync_ReportsLengthsOfTheWholeContent()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "abcdef"));

        var result = await store.EditAsync("m1", "cd", "CDCD");

        Assert.AreEqual(6, result.OldLength);
        Assert.AreEqual(8, result.NewLength);
    }

    [TestMethod]
    public async Task EditAsync_EmptyNewText_DeletesTheMatch()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "A fact, with an aside, worth keeping"));

        var result = await store.EditAsync("m1", ", with an aside,", "");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual("A fact worth keeping", (await store.GetAsync("m1"))!.Content);
    }

    [TestMethod]
    public async Task EditAsync_ReplaceAll_ReplacesEveryOccurrence()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "chicago and chicago and chicago"));

        var result = await store.EditAsync("m1", "chicago", "denver", replaceAll: true);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(3, result.ReplacementCount);
        Assert.AreEqual("denver and denver and denver", (await store.GetAsync("m1"))!.Content);
    }

    [TestMethod]
    public async Task EditAsync_PersistsToDisk()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "Old wording", category: "user-preferences"));

        await store.EditAsync("m1", "Old", "New");

        var json = await File.ReadAllTextAsync(Path.Combine(_tempDir, "user-preferences", "m1.json"));
        var onDisk = JsonSerializer.Deserialize<MemoryEntry>(json, JsonOptions);
        Assert.AreEqual("New wording", onDisk!.Content);
    }

    [TestMethod]
    public async Task EditAsync_LeavesNoTemporaryFilesBehind()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "Old wording"));

        await store.EditAsync("m1", "Old", "New");

        var strays = Directory.EnumerateFiles(_tempDir, "*.tmp", SearchOption.AllDirectories).ToList();
        Assert.AreEqual(0, strays.Count, "The atomic write must not leave its temp file behind.");
    }

    // ── Provenance ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditAsync_PreservesEveryFieldExceptContentAndUpdatedAt()
    {
        var store = CreateStore();
        var created = DateTimeOffset.UtcNow.AddDays(-400);
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-3);
        var original = new MemoryEntry(
            "m1",
            "User lives in Chicago and works remotely",
            Category: "user-preferences/location",
            Tags: ["location", "chicago"],
            CreatedAt: created,
            UpdatedAt: created.AddDays(1),
            Metadata: new Dictionary<string, string> { ["subjectTime"] = "2019" },
            ImportanceScore: 0.87f)
        {
            LastSeenAt = lastSeen,
            ReinforcementCount = 214,
            SupersededBy = "other-entry",
            ArchivedAt = created.AddDays(2),
            ArchiveReason = "merged into other-entry"
        };
        await store.SaveAsync(original);

        var before = DateTimeOffset.UtcNow;
        var result = await store.EditAsync("m1", "Chicago", "Minneapolis");

        Assert.IsTrue(result.IsSuccess, result.Error);
        var edited = await store.GetAsync("m1");
        Assert.IsNotNull(edited);

        Assert.AreEqual("User lives in Minneapolis and works remotely", edited.Content);
        Assert.IsTrue(edited.UpdatedAt >= before, "UpdatedAt should move to now.");

        Assert.AreEqual("m1", edited.Id, "The id must survive — it is how the entry is referenced.");
        Assert.AreEqual(created, edited.CreatedAt);
        Assert.AreEqual(lastSeen, edited.LastSeenAt, "An edit is not a reinforcement.");
        Assert.AreEqual(214, edited.ReinforcementCount);
        Assert.AreEqual(0.87f, edited.ImportanceScore);
        Assert.AreEqual("user-preferences/location", edited.Category);
        CollectionAssert.AreEqual(new[] { "location", "chicago" }, edited.Tags.ToArray());
        Assert.AreEqual("2019", edited.Metadata!["subjectTime"]);
        Assert.AreEqual("other-entry", edited.SupersededBy);
        Assert.AreEqual(created.AddDays(2), edited.ArchivedAt);
        Assert.AreEqual("merged into other-entry", edited.ArchiveReason);
    }

    [TestMethod]
    public async Task EditAsync_KeepsTheEntryInItsExistingCategoryFile()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "Old wording", category: "project-context/rockbot"));

        await store.EditAsync("m1", "Old", "New");

        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "project-context", "rockbot", "m1.json")));
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "m1.json")));
    }

    // ── Refusals ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditAsync_UnknownId_RefusesWithAnActionableMessage()
    {
        var store = CreateStore();

        var result = await store.EditAsync("nope", "a", "b");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "nope");
        StringAssert.Contains(result.Error, "Search memory");
    }

    [TestMethod]
    public async Task EditAsync_TextNotFound_Refuses_AndLeavesContentUntouched()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "User prefers concise answers"));

        var result = await store.EditAsync("m1", "verbose", "concise");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "not found");
        Assert.AreEqual("User prefers concise answers", (await store.GetAsync("m1"))!.Content);
    }

    [TestMethod]
    public async Task EditAsync_AmbiguousMatch_Refuses_AndWritesNothing()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "dogs and more dogs"));
        var stampBefore = (await store.GetAsync("m1"))!.UpdatedAt;

        var result = await store.EditAsync("m1", "dogs", "cats");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "occurs 2 times");
        var after = await store.GetAsync("m1");
        Assert.AreEqual("dogs and more dogs", after!.Content);
        Assert.AreEqual(stampBefore, after.UpdatedAt, "A refused edit must not bump UpdatedAt.");
    }

    [TestMethod]
    public async Task EditAsync_IdenticalOldAndNew_Refuses()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "unchanged"));

        var result = await store.EditAsync("m1", "unchanged", "unchanged");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "identical");
    }

    [TestMethod]
    public async Task EditAsync_EmptyOldText_Refuses()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "some content"));

        var result = await store.EditAsync("m1", "", "something");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "must not be empty");
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditAsync_ConcurrentEdits_AllLand()
    {
        // Each edit targets a distinct marker, so a lost read-modify-write cycle shows up as
        // a marker that is still present after every call reported success.
        // Markers are zero-padded so none is a prefix of another — otherwise "M1" would match
        // inside "M10" and the edit would be refused as ambiguous rather than tested.
        const int markers = 12;
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", string.Join(" ", Enumerable.Range(0, markers).Select(i => $"M{i:D2}"))));

        var results = await Task.WhenAll(
            Enumerable.Range(0, markers).Select(i => store.EditAsync("m1", $"M{i:D2}", $"X{i:D2}")));

        Assert.IsTrue(
            results.All(r => r.IsSuccess),
            "Every concurrent edit should apply: " + string.Join("; ", results.Where(r => !r.IsSuccess).Select(r => r.Error)));
        var content = (await store.GetAsync("m1"))!.Content;
        for (var i = 0; i < markers; i++)
            StringAssert.Contains(content, $"X{i:D2}", $"Edit of M{i:D2} was overwritten by a concurrent edit.");
    }

    [TestMethod]
    public async Task EditAsync_ConcurrentWithSave_DoesNotCorruptTheEntry()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "alpha bravo"));

        var edit = store.EditAsync("m1", "alpha", "ALPHA");
        var save = store.SaveAsync(Entry("m2", "unrelated"));
        await Task.WhenAll(edit, save);

        Assert.IsTrue(edit.Result.IsSuccess, edit.Result.Error);
        Assert.AreEqual("ALPHA bravo", (await store.GetAsync("m1"))!.Content);
        Assert.AreEqual("unrelated", (await store.GetAsync("m2"))!.Content);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private FileMemoryStore CreateStore() =>
        new(Options.Create(new MemoryOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            Options.Create(new EmbeddingOptions()),
            NullLogger<FileMemoryStore>.Instance,
            EmbeddingTextPreparer.ForTests());

    private static MemoryEntry Entry(string id, string content, string? category = null) =>
        new(id, content, category, [], DateTimeOffset.UtcNow);
}

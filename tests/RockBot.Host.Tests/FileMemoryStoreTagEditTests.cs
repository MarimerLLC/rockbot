using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers <see cref="FileMemoryStore.EditTagsAsync"/> — changing an entry's tags in place, so a
/// corrected entry stops matching searches for the thing it no longer says without losing its
/// identity and history (#824).
/// </summary>
[TestClass]
public sealed class FileMemoryStoreTagEditTests
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
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-memory-tag-edit-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Success ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditTagsAsync_Add_AppendsTheTag_AndReportsBeforeAndAfter()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "Balanced tier routing", tags: ["routing"]));

        var result = await store.EditTagsAsync("m1", ["claude-haiku-5-5"], []);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse(result.IsNoOp);
        CollectionAssert.AreEqual(new[] { "routing" }, result.Before.ToArray());
        CollectionAssert.AreEqual(new[] { "routing", "claude-haiku-5-5" }, result.After.ToArray());
        CollectionAssert.AreEqual(new[] { "claude-haiku-5-5" }, result.Added.ToArray());
        Assert.AreEqual(0, result.Removed.Count);
        CollectionAssert.AreEqual(new[] { "routing", "claude-haiku-5-5" }, (await store.GetAsync("m1"))!.Tags.ToArray());
    }

    [TestMethod]
    public async Task EditTagsAsync_Remove_IsCaseInsensitive_AndKeepsTheRestInOrder()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "Balanced tier routing", tags: ["routing", "Claude-Sonnet-5-5", "llm"]));

        var result = await store.EditTagsAsync("m1", [], ["claude-sonnet-5-5"]);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(new[] { "Claude-Sonnet-5-5" }, result.Removed.ToArray(),
            "Removed reports the tag as it was stored.");
        CollectionAssert.AreEqual(new[] { "routing", "llm" }, (await store.GetAsync("m1"))!.Tags.ToArray());
    }

    [TestMethod]
    public async Task EditTagsAsync_AddAndRemoveTogether()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "Balanced tier routing", tags: ["routing", "claude-sonnet-5-5"]));

        var result = await store.EditTagsAsync("m1", ["claude-haiku-5-5"], ["claude-sonnet-5-5"]);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(new[] { "routing", "claude-haiku-5-5" }, result.After.ToArray());
        CollectionAssert.AreEqual(new[] { "routing", "claude-haiku-5-5" }, (await store.GetAsync("m1"))!.Tags.ToArray());
    }

    [TestMethod]
    public async Task EditTagsAsync_PersistsToDisk_InTheExistingCategoryFile()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "Balanced tier routing", category: "infrastructure/llm", tags: ["old"]));

        await store.EditTagsAsync("m1", ["new"], ["old"]);

        var json = await File.ReadAllTextAsync(Path.Combine(_tempDir, "infrastructure", "llm", "m1.json"));
        var onDisk = JsonSerializer.Deserialize<MemoryEntry>(json, JsonOptions);
        CollectionAssert.AreEqual(new[] { "new" }, onDisk!.Tags.ToArray());
        Assert.AreEqual("infrastructure/llm", onDisk.Category);
        Assert.AreEqual(0, Directory.EnumerateFiles(_tempDir, "*.tmp", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task EditTagsAsync_NormalisesWhitespaceEmptiesAndDuplicates()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "content", tags: ["keep"]));

        var result = await store.EditTagsAsync("m1", ["  alpha ", "", "ALPHA", "   ", "beta"], [" ", ""]);

        Assert.IsTrue(result.IsSuccess, result.Error);
        CollectionAssert.AreEqual(new[] { "keep", "alpha", "beta" }, result.After.ToArray());
    }

    // ── Search surface ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditTagsAsync_RemovedTagNoLongerMatchesBm25_AddedTagDoes()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "The balanced tier runs Haiku first", tags: ["sonnet"]));
        await store.SaveAsync(Entry("m2", "Unrelated note about gardening", tags: ["plants"]));

        Assert.IsTrue((await Search(store, "sonnet")).Any(e => e.Id == "m1"), "precondition: the stale tag matches");

        await store.EditTagsAsync("m1", ["openrouter"], ["sonnet"]);

        Assert.IsFalse((await Search(store, "sonnet")).Any(e => e.Id == "m1"),
            "A removed tag must stop contributing to BM25 matching.");
        Assert.IsTrue((await Search(store, "openrouter")).Any(e => e.Id == "m1"),
            "An added tag must contribute to BM25 matching.");
    }

    [TestMethod]
    public async Task EditTagsAsync_RegeneratesTheEmbeddingFromTheNewTags()
    {
        var generator = new RecordingEmbeddingGenerator();
        var store = CreateStore(generator);
        await store.SaveAsync(Entry("m1", "The balanced tier runs Haiku first", tags: ["stale-tag"]));

        var result = await store.EditTagsAsync("m1", ["fresh-tag"], ["stale-tag"]);
        Assert.IsTrue(result.IsSuccess, result.Error);

        // Re-embedding is fire-and-forget, as on save and content edit, so wait for it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !generator.Inputs.Any(IsRegenerated))
            await Task.Delay(20);

        Assert.IsTrue(generator.Inputs.Any(IsRegenerated),
            "Expected an embedding generated from the new tags. Inputs: " + string.Join(" | ", generator.Inputs));

        static bool IsRegenerated(string text) =>
            text.Contains("fresh-tag", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("stale-tag", StringComparison.OrdinalIgnoreCase);
    }

    // ── Provenance ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditTagsAsync_PreservesEveryFieldExceptTagsAndUpdatedAt()
    {
        var store = CreateStore();
        var created = DateTimeOffset.UtcNow.AddDays(-400);
        var lastSeen = DateTimeOffset.UtcNow.AddDays(-3);
        var original = new MemoryEntry(
            "m1",
            "Balanced tier runs Haiku 5.5 first",
            Category: "infrastructure/llm",
            Tags: ["routing", "claude-sonnet-5-5"],
            CreatedAt: created,
            UpdatedAt: created.AddDays(1),
            Metadata: new Dictionary<string, string> { ["source"] = "tool:get_llm_config" },
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
        var result = await store.EditTagsAsync("m1", ["claude-haiku-5-5"], ["claude-sonnet-5-5"]);

        Assert.IsTrue(result.IsSuccess, result.Error);
        var edited = await store.GetAsync("m1");
        Assert.IsNotNull(edited);

        CollectionAssert.AreEqual(new[] { "routing", "claude-haiku-5-5" }, edited.Tags.ToArray());
        Assert.IsTrue(edited.UpdatedAt >= before, "UpdatedAt should move to now.");

        Assert.AreEqual("m1", edited.Id);
        Assert.AreEqual("Balanced tier runs Haiku 5.5 first", edited.Content);
        Assert.AreEqual(created, edited.CreatedAt);
        Assert.AreEqual(lastSeen, edited.LastSeenAt, "A tag edit is not a reinforcement.");
        Assert.AreEqual(214, edited.ReinforcementCount);
        Assert.AreEqual(0.87f, edited.ImportanceScore);
        Assert.AreEqual("infrastructure/llm", edited.Category);
        Assert.AreEqual("tool:get_llm_config", edited.Metadata!["source"]);
        Assert.AreEqual("other-entry", edited.SupersededBy);
        Assert.AreEqual(created.AddDays(2), edited.ArchivedAt);
        Assert.AreEqual("merged into other-entry", edited.ArchiveReason);
    }

    // ── No-ops ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditTagsAsync_RemovingAMissingTag_IsAReportedNoOp_ThatWritesNothing()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "content", tags: ["routing"]));
        var stampBefore = (await store.GetAsync("m1"))!.UpdatedAt;
        var path = Path.Combine(_tempDir, "m1.json");
        var writtenBefore = File.GetLastWriteTimeUtc(path);

        var result = await store.EditTagsAsync("m1", [], ["not-there"]);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.IsNoOp);
        CollectionAssert.AreEqual(new[] { "not-there" }, result.NotPresent.ToArray());
        CollectionAssert.AreEqual(new[] { "routing" }, result.After.ToArray());
        Assert.AreEqual(stampBefore, (await store.GetAsync("m1"))!.UpdatedAt, "A no-op must not bump UpdatedAt.");
        Assert.AreEqual(writtenBefore, File.GetLastWriteTimeUtc(path), "A no-op must not rewrite the file.");
    }

    [TestMethod]
    public async Task EditTagsAsync_AddingAPresentTag_IsAReportedNoOp()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "content", tags: ["Routing"]));

        var result = await store.EditTagsAsync("m1", ["routing"], []);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsTrue(result.IsNoOp);
        CollectionAssert.AreEqual(new[] { "routing" }, result.AlreadyPresent.ToArray());
        CollectionAssert.AreEqual(new[] { "Routing" }, (await store.GetAsync("m1"))!.Tags.ToArray());
    }

    [TestMethod]
    public async Task EditTagsAsync_PartialNoOp_ReportsTheSkippedTagsAlongsideTheChange()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "content", tags: ["a"]));

        var result = await store.EditTagsAsync("m1", ["a", "b"], ["z"]);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.IsFalse(result.IsNoOp);
        CollectionAssert.AreEqual(new[] { "b" }, result.Added.ToArray());
        CollectionAssert.AreEqual(new[] { "a" }, result.AlreadyPresent.ToArray());
        CollectionAssert.AreEqual(new[] { "z" }, result.NotPresent.ToArray());
    }

    // ── Refusals ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditTagsAsync_UnknownId_RefusesWithAnActionableMessage()
    {
        var store = CreateStore();

        var result = await store.EditTagsAsync("nope", ["a"], []);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "nope");
        StringAssert.Contains(result.Error, "Search memory");
    }

    [TestMethod]
    public async Task EditTagsAsync_NothingRequested_Refuses()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "content", tags: ["a"]));

        var result = await store.EditTagsAsync("m1", [" "], []);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "Nothing to change");
    }

    [TestMethod]
    public async Task EditTagsAsync_SameTagInAddAndRemove_Refuses_AndWritesNothing()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "content", tags: ["a"]));

        var result = await store.EditTagsAsync("m1", ["b"], ["B"]);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "both add and remove");
        CollectionAssert.AreEqual(new[] { "a" }, (await store.GetAsync("m1"))!.Tags.ToArray());
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditTagsAsync_ConcurrentTagEdits_AllLand()
    {
        const int count = 12;
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "content", tags: []));

        var results = await Task.WhenAll(
            Enumerable.Range(0, count).Select(i => store.EditTagsAsync("m1", [$"t{i:D2}"], [])));

        Assert.IsTrue(results.All(r => r.IsSuccess));
        var tags = (await store.GetAsync("m1"))!.Tags;
        for (var i = 0; i < count; i++)
            CollectionAssert.Contains(tags.ToList(), $"t{i:D2}", $"Tag t{i:D2} was lost to a concurrent tag edit.");
    }

    [TestMethod]
    public async Task EditTagsAsync_ConcurrentWithContentEditAndSave_LosesNothing()
    {
        var store = CreateStore();
        await store.SaveAsync(Entry("m1", "alpha bravo", tags: ["old"]));

        var tagEdit = store.EditTagsAsync("m1", ["new"], ["old"]);
        var contentEdit = store.EditAsync("m1", "alpha", "ALPHA");
        var save = store.SaveAsync(Entry("m2", "unrelated"));
        await Task.WhenAll(tagEdit, contentEdit, save);

        Assert.IsTrue(tagEdit.Result.IsSuccess, tagEdit.Result.Error);
        Assert.IsTrue(contentEdit.Result.IsSuccess, contentEdit.Result.Error);

        var m1 = (await store.GetAsync("m1"))!;
        Assert.AreEqual("ALPHA bravo", m1.Content, "The content edit was lost to the tag edit.");
        CollectionAssert.AreEqual(new[] { "new" }, m1.Tags.ToArray(), "The tag edit was lost to the content edit.");
        Assert.AreEqual("unrelated", (await store.GetAsync("m2"))!.Content);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task<IReadOnlyList<MemoryEntry>> Search(FileMemoryStore store, string query) =>
        store.SearchAsync(new MemorySearchCriteria(Query: query, MaxResults: 10));

    private FileMemoryStore CreateStore(IEmbeddingGenerator<string, Embedding<float>>? generator = null) =>
        new(Options.Create(new MemoryOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            Options.Create(new EmbeddingOptions()),
            NullLogger<FileMemoryStore>.Instance,
            EmbeddingTextPreparer.ForTests(),
            generator);

    private static MemoryEntry Entry(string id, string content, string? category = null, string[]? tags = null) =>
        new(id, content, category, tags ?? [], DateTimeOffset.UtcNow);

    private sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public ConcurrentQueue<string> Inputs { get; } = new();

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
            {
                Inputs.Enqueue(value);
                list.Add(new Embedding<float>(new float[] { 1f, 0f, 0f }));
            }
            return Task.FromResult(list);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

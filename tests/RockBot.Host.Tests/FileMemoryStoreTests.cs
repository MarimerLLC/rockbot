using System.Text.Json;
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

    // ── BM25 ranking ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SearchAsync_WithQuery_RanksMoreRelevantEntryFirst()
    {
        var store = CreateStore();
        // Entry "weak" mentions "concert" once; entry "strong" mentions it three times
        await store.SaveAsync(CreateEntry("weak", "I went to a concert last summer"));
        await store.SaveAsync(CreateEntry("strong", "concert concert concert — huge music fan, loves concerts"));

        var results = await store.SearchAsync(new MemorySearchCriteria(Query: "concert music"));

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("strong", results[0].Id, "Higher-frequency match should rank first");
    }

    [TestMethod]
    public async Task SearchAsync_WithQuery_MultiWordQuery_MatchesEitherTerm()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("music", "loves rock music and concerts"));
        await store.SaveAsync(CreateEntry("sport", "plays basketball every weekend"));
        await store.SaveAsync(CreateEntry("both",  "music and sport are daily hobbies"));

        var results = await store.SearchAsync(new MemorySearchCriteria(Query: "rock music"));

        // "music" and "both" contain at least one query term; "sport" contains neither
        Assert.IsTrue(results.Any(r => r.Id == "music"));
        Assert.IsTrue(results.Any(r => r.Id == "both"));
        Assert.IsFalse(results.Any(r => r.Id == "sport"));
    }

    [TestMethod]
    public async Task SearchAsync_WithQuery_TwoWordPhrase_BoostsAdjacentMatch()
    {
        var store = CreateStore();
        // "adjacent" has both words next to each other; "scattered" has them separated
        await store.SaveAsync(CreateEntry("adjacent",  "Rocky loves rock music at every festival"));
        await store.SaveAsync(CreateEntry("scattered", "Rocky plays rock. He also enjoys music sometimes."));

        var results = await store.SearchAsync(new MemorySearchCriteria(Query: "rock music"));

        Assert.AreEqual(2, results.Count);
        // "adjacent" contains the phrase "rock music" → phrase bonus → should score higher
        Assert.AreEqual("adjacent", results[0].Id, "Phrase match should outrank scattered terms");
    }

    [TestMethod]
    public async Task SearchAsync_WithQuery_NoMatchingTerms_ReturnsEmpty()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("1", "The cat sat on the mat"));
        await store.SaveAsync(CreateEntry("2", "Dogs bark loudly outside"));

        var results = await store.SearchAsync(new MemorySearchCriteria(Query: "quantum physics"));

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task SearchAsync_WithQuery_ShortQueryTokensFiltered_FallsBackToEmpty()
    {
        // Tokens shorter than 3 chars are stripped; "hi" alone yields no tokens → empty
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("1", "Hello world hi there"));

        var results = await store.SearchAsync(new MemorySearchCriteria(Query: "hi"));

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task SearchAsync_NoQuery_ReturnsMostRecentFirst()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new MemoryEntry("old",    "Old entry",    null, [], now.AddDays(-10)));
        await store.SaveAsync(new MemoryEntry("middle", "Middle entry", null, [], now.AddDays(-5)));
        await store.SaveAsync(new MemoryEntry("recent", "Recent entry", null, [], now));

        var results = await store.SearchAsync(new MemorySearchCriteria());

        Assert.AreEqual(3, results.Count);
        Assert.AreEqual("recent", results[0].Id);
        Assert.AreEqual("middle", results[1].Id);
        Assert.AreEqual("old",    results[2].Id);
    }

    [TestMethod]
    public async Task SearchAsync_NoQuery_OrdersByLastSeenAtDescending()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        // Reinforced recently: old creation, but LastSeenAt was bumped by a real save-event merge
        var reinforced = new MemoryEntry("reinforced", "Reinforced entry", null, [],
            CreatedAt: now.AddDays(-30), UpdatedAt: now)
        {
            LastSeenAt = now.AddHours(-2),
            ReinforcementCount = 3
        };

        // Dream-rewritten only: old creation, UpdatedAt bumped by dream housekeeping, but
        // LastSeenAt still reflects the original creation (no real reinforcement).
        var dreamOnly = new MemoryEntry("dream-only", "Dream-rewritten entry", null, [],
            CreatedAt: now.AddDays(-30), UpdatedAt: now);
        // LastSeenAt defaults to CreatedAt (-30d)

        var fresh = new MemoryEntry("fresh", "Freshly created entry", null, [],
            CreatedAt: now.AddDays(-1));
        // LastSeenAt defaults to -1d

        await store.SaveAsync(reinforced);
        await store.SaveAsync(dreamOnly);
        await store.SaveAsync(fresh);

        var results = await store.SearchAsync(new MemorySearchCriteria());

        Assert.AreEqual("reinforced", results[0].Id, "Most-recently reinforced entry ranks first.");
        Assert.AreEqual("fresh", results[1].Id, "A fresh single-observation entry outranks a dream-rewritten stale one.");
        Assert.AreEqual("dream-only", results[2].Id, "Dream housekeeping (UpdatedAt bump) must not promote ranking.");
    }

    [TestMethod]
    public async Task SearchAsync_WithQuery_TagsContributeToScore()
    {
        var store = CreateStore();
        // "tagged" has the word only in tags; "content" has it in content
        await store.SaveAsync(CreateEntry("tagged",  "Rocky is an avid outdoorsman", tags: ["fishing", "hiking"]));
        await store.SaveAsync(CreateEntry("content", "Rocky enjoys fishing in frozen lakes"));

        var results = await store.SearchAsync(new MemorySearchCriteria(Query: "fishing"));

        // Both should score > 0 since tags are included in the document text
        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Any(r => r.Id == "tagged"));
        Assert.IsTrue(results.Any(r => r.Id == "content"));
    }

    // ── Regex mode ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Search_RegexMode_MatchesLiteralToken()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("v0", "deploy v0.10.30 to staging"));
        await store.SaveAsync(CreateEntry("other", "unrelated content"));

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Query: @"v\d+\.\d+\.\d+", Mode: MemorySearchMode.Regex));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("v0", results[0].Id);
    }

    [TestMethod]
    public async Task Search_RegexMode_MatchesMemoryIdInPathName()
    {
        var store = CreateStore();
        // Content does not mention "openrouter-key" — only the id does.
        await store.SaveAsync(CreateEntry("openrouter-key-rotation", "An unrelated note about rotation."));
        await store.SaveAsync(CreateEntry("foo", "Different entry."));

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Query: "openrouter-key", Mode: MemorySearchMode.Regex));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("openrouter-key-rotation", results[0].Id);
    }

    [TestMethod]
    public async Task Search_RegexMode_MatchesCategoryInPathName()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("foo", "Some content", category: "project-context"));
        await store.SaveAsync(CreateEntry("bar", "Other content", category: "user-preferences"));

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Query: @"^project-context/", Mode: MemorySearchMode.Regex));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("foo", results[0].Id);
    }

    [TestMethod]
    public async Task Search_RegexMode_DoesNotMatchOnDiskFilePath()
    {
        // The on-disk path ends in ".json" but the regex surface deliberately omits it.
        // A pattern that hits the storage layout (.json suffix) must not match anything.
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("entry-1", "Some plain content"));

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Query: @"\.json$", Mode: MemorySearchMode.Regex));

        Assert.AreEqual(0, results.Count, "Storage-layout details must not be exposed to regex matches.");
    }

    [TestMethod]
    public async Task Search_RegexMode_DefaultIsCaseInsensitive()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("h", "Helm release in cluster"));

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Query: "helm", Mode: MemorySearchMode.Regex));

        Assert.AreEqual(1, results.Count);
    }

    [TestMethod]
    public async Task Search_RegexMode_CaseSensitive_RespectsFlag()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("h", "Helm release in cluster"));

        var lowerResults = await store.SearchAsync(new MemorySearchCriteria(
            Query: "helm", Mode: MemorySearchMode.Regex, RegexCaseSensitive: true));
        Assert.AreEqual(0, lowerResults.Count);

        var upperResults = await store.SearchAsync(new MemorySearchCriteria(
            Query: "Helm", Mode: MemorySearchMode.Regex, RegexCaseSensitive: true));
        Assert.AreEqual(1, upperResults.Count);
    }

    [TestMethod]
    public async Task Search_RegexMode_HonorsCategoryFilter()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("a", "shared keyword", category: "project-context"));
        await store.SaveAsync(CreateEntry("b", "shared keyword", category: "user-preferences"));

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Query: "shared", Category: "project-context", Mode: MemorySearchMode.Regex));

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("a", results[0].Id);
    }

    [TestMethod]
    public async Task Search_RegexMode_RespectsMaxResults()
    {
        var store = CreateStore();
        for (int i = 0; i < 10; i++)
            await store.SaveAsync(CreateEntry($"e{i}", "matching content"));

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Query: "matching", Mode: MemorySearchMode.Regex, MaxResults: 3));

        Assert.AreEqual(3, results.Count);
    }

    [TestMethod]
    public async Task Search_RegexMode_InvalidPattern_Throws()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("a", "anything"));

        await Assert.ThrowsExactlyAsync<MemorySearchException>(async () =>
        {
            await store.SearchAsync(new MemorySearchCriteria(
                Query: "[", Mode: MemorySearchMode.Regex));
        });
    }

    [TestMethod]
    public async Task Search_RegexMode_NoMatches_ReturnsEmpty()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateEntry("a", "the cat sat on the mat"));

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Query: "nothing-here", Mode: MemorySearchMode.Regex));

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task Search_RegexMode_OrdersByImportanceThenLastSeen()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;

        await store.SaveAsync(new MemoryEntry("low", "shared content", null, [], now,
            ImportanceScore: 0.2f) { LastSeenAt = now });
        await store.SaveAsync(new MemoryEntry("highOld", "shared content", null, [], now.AddDays(-10),
            ImportanceScore: 0.9f) { LastSeenAt = now.AddDays(-10) });
        await store.SaveAsync(new MemoryEntry("highRecent", "shared content", null, [], now,
            ImportanceScore: 0.9f) { LastSeenAt = now });

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Query: "shared", Mode: MemorySearchMode.Regex));

        Assert.AreEqual(3, results.Count);
        Assert.AreEqual("highRecent", results[0].Id, "Highest importance, most recent wins.");
        Assert.AreEqual("highOld", results[1].Id, "Same importance, older LastSeenAt comes second.");
        Assert.AreEqual("low", results[2].Id);
    }

    [TestMethod]
    public async Task Search_RegexMode_NullQuery_FallsBackToNoQueryPath()
    {
        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new MemoryEntry("old", "old", null, [], now.AddDays(-5)));
        await store.SaveAsync(new MemoryEntry("new", "new", null, [], now));

        var results = await store.SearchAsync(new MemorySearchCriteria(
            Mode: MemorySearchMode.Regex));

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("new", results[0].Id, "Null query falls through to LastSeenAt ordering.");
    }

    [TestMethod]
    public void Search_RegexMode_PathologicalPattern_ThrowsTimeout()
    {
        // Catastrophic backtracking: (a+)+$ on a string that's all 'a's plus a trailing 'b'.
        // Force a tiny per-entry timeout so the test stays under ~200ms.
        var entry = new MemoryEntry("evil", new string('a', 60) + "b", null, [], DateTimeOffset.UtcNow);

        Assert.ThrowsExactly<MemorySearchException>(() =>
            RegexMatcher.MatchEntries(
                new[] { entry },
                @"(a+)+$",
                caseSensitive: false,
                maxResults: 10,
                FileMemoryStore.BuildRegexSurface,
                perEntryTimeout: TimeSpan.FromMilliseconds(50),
                overallBudget: TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void Search_RegexMode_OverallBudget_BoundsTotalScan()
    {
        // Build many candidates and force per-entry surface generation to be slow via the
        // documentText delegate. Each "entry" adds ~5ms to wall clock; with a 30ms overall
        // budget we expect to throw before scanning all 200.
        var now = DateTimeOffset.UtcNow;
        var candidates = Enumerable.Range(0, 200)
            .Select(i => new MemoryEntry($"e{i}", "content", null, [], now))
            .ToList();

        var ex = Assert.ThrowsExactly<MemorySearchException>(() =>
            RegexMatcher.MatchEntries(
                candidates,
                "content",
                caseSensitive: false,
                maxResults: 10,
                e => { Thread.Sleep(5); return e.Content; },
                perEntryTimeout: TimeSpan.FromSeconds(1),
                overallBudget: TimeSpan.FromMilliseconds(30)));

        StringAssert.Contains(ex.Message, "/200");
        StringAssert.Contains(ex.Message, "exceeded");
    }

    // ── Tokenizer unit tests ──────────────────────────────────────────────────

    [TestMethod]
    public void Tokenize_FiltersShortTokens()
    {
        var tokens = Bm25Ranker.Tokenize("hi is a cat");
        // "hi"=2, "is"=2, "a"=1 filtered; "cat"=3 kept
        CollectionAssert.AreEqual(new[] { "cat" }, tokens);
    }

    [TestMethod]
    public void Tokenize_LowercasesInput()
    {
        var tokens = Bm25Ranker.Tokenize("Rock Music FESTIVAL");
        CollectionAssert.AreEqual(new[] { "rock", "music", "festival" }, tokens);
    }

    [TestMethod]
    public void Tokenize_SplitsOnNonAlphanumeric()
    {
        var tokens = Bm25Ranker.Tokenize("rock-music festival_2026");
        CollectionAssert.AreEqual(new[] { "rock", "music", "festival", "2026" }, tokens);
    }

    [TestMethod]
    public void GetDocumentText_IncludesContentTagsAndCategory()
    {
        var entry = new MemoryEntry("id", "Rocky loves festivals", "user-preferences/music",
            ["rock", "live-music"], DateTimeOffset.UtcNow);

        var text = FileMemoryStore.GetDocumentText(entry);

        Assert.IsTrue(text.Contains("Rocky loves festivals"));
        Assert.IsTrue(text.Contains("rock"));
        Assert.IsTrue(text.Contains("live-music"));
        // Category slashes and hyphens become spaces
        Assert.IsTrue(text.Contains("user preferences music"));
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
            Options.Create(new EmbeddingOptions()),
            NullLogger<FileMemoryStore>.Instance);
    }

    // ── Importance boost ─────────────────────────────────────────────────────

    [TestMethod]
    public void ImportanceBoost_DefaultScore_Returns0_75()
    {
        var boost = FileMemoryStore.ImportanceBoost(0.5f);
        Assert.AreEqual(0.75, boost, 0.001);
    }

    [TestMethod]
    public void ImportanceBoost_MaxScore_Returns1_0()
    {
        var boost = FileMemoryStore.ImportanceBoost(1.0f);
        Assert.AreEqual(1.0, boost, 0.001);
    }

    [TestMethod]
    public void ImportanceBoost_ZeroScore_Returns0_5()
    {
        var boost = FileMemoryStore.ImportanceBoost(0.0f);
        Assert.AreEqual(0.5, boost, 0.001);
    }

    [TestMethod]
    public void ImportanceBoost_ClampsAbove1()
    {
        var boost = FileMemoryStore.ImportanceBoost(1.5f);
        Assert.AreEqual(1.0, boost, 0.001);
    }

    [TestMethod]
    public void ImportanceBoost_ClampsBelow0()
    {
        var boost = FileMemoryStore.ImportanceBoost(-0.5f);
        Assert.AreEqual(0.5, boost, 0.001);
    }

    [TestMethod]
    public async Task SearchAsync_BM25_HighImportance_RanksHigher()
    {
        var store = CreateStore();

        // Two entries with the same content but different importance
        await store.SaveAsync(CreateEntry("low", "kubernetes deployment pattern", importance: 0.2f));
        await store.SaveAsync(CreateEntry("high", "kubernetes deployment pattern", importance: 0.9f));

        var results = await store.SearchAsync(
            new MemorySearchCriteria(Query: "kubernetes deployment", MaxResults: 10));

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("high", results[0].Id, "High-importance entry should rank first");
        Assert.AreEqual("low", results[1].Id);
    }

    [TestMethod]
    public async Task SaveAsync_And_GetAsync_RoundTrips_ImportanceScore()
    {
        var store = CreateStore();
        var entry = CreateEntry("imp-1", "Critical decision", importance: 0.85f);

        await store.SaveAsync(entry);
        var result = await store.GetAsync("imp-1");

        Assert.IsNotNull(result);
        Assert.AreEqual(0.85f, result.ImportanceScore);
    }

    [TestMethod]
    public void MemoryEntry_DefaultImportanceScore_Is0_5()
    {
        var entry = new MemoryEntry("id", "content", null, [], DateTimeOffset.UtcNow);
        Assert.AreEqual(0.5f, entry.ImportanceScore);
    }

    private static MemoryEntry CreateEntry(
        string id,
        string content,
        string? category = null,
        string[]? tags = null,
        float importance = 0.5f)
    {
        return new MemoryEntry(
            id,
            content,
            category,
            tags ?? [],
            DateTimeOffset.UtcNow,
            ImportanceScore: importance);
    }

    // ── Temporal reinforcement fields (LastSeenAt, ReinforcementCount) ───────

    [TestMethod]
    public void MemoryEntry_NewInstance_LastSeenAtDefaultsToCreatedAt()
    {
        var created = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var entry = new MemoryEntry("id1", "content", null, [], created);

        Assert.AreEqual(created, entry.LastSeenAt);
        Assert.AreEqual(1, entry.ReinforcementCount);
    }

    [TestMethod]
    public void MemoryEntry_DeserializeFromLegacyJson_UsesCreatedAtAsLastSeenAt()
    {
        // JSON as written by a pre-time-feature build: no lastSeenAt, no reinforcementCount
        var legacyJson = """
            {
              "id": "legacy1",
              "content": "durable fact",
              "category": "user-preferences",
              "tags": ["fact"],
              "createdAt": "2024-01-15T12:00:00+00:00",
              "updatedAt": null,
              "metadata": null,
              "importanceScore": 0.5
            }
            """;

        var entry = JsonSerializer.Deserialize<MemoryEntry>(
            legacyJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.IsNotNull(entry);
        Assert.AreEqual("legacy1", entry.Id);
        Assert.AreEqual(new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero), entry.CreatedAt);
        Assert.AreEqual(entry.CreatedAt, entry.LastSeenAt,
            "Legacy JSON without lastSeenAt should default to CreatedAt via init-only default.");
        Assert.AreEqual(1, entry.ReinforcementCount,
            "Legacy JSON without reinforcementCount should default to 1.");
    }

    [TestMethod]
    public async Task MemoryEntry_RoundtripThroughStore_PreservesNewFields()
    {
        var store = CreateStore();
        var created = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var lastSeen = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
        var entry = new MemoryEntry(
            "reinforced-1",
            "Fact seen many times",
            Category: "user-preferences",
            Tags: ["fact"],
            CreatedAt: created)
        {
            LastSeenAt = lastSeen,
            ReinforcementCount = 4
        };

        await store.SaveAsync(entry);
        var result = await store.GetAsync("reinforced-1");

        Assert.IsNotNull(result);
        Assert.AreEqual(created, result.CreatedAt);
        Assert.AreEqual(lastSeen, result.LastSeenAt);
        Assert.AreEqual(4, result.ReinforcementCount);
    }

    [TestMethod]
    public async Task MemoryEntry_SubjectTimeMetadata_RoundtripsViaStore()
    {
        var store = CreateStore();
        var metadata = new Dictionary<string, string>
        {
            ["subjectTimeStart"] = "1995",
            ["subjectTimeEnd"] = "2003"
        };
        var entry = new MemoryEntry(
            "chicago-years",
            "User lived in Chicago",
            Category: "user-preferences/location",
            Tags: ["chicago"],
            CreatedAt: DateTimeOffset.UtcNow,
            Metadata: metadata);

        await store.SaveAsync(entry);
        var result = await store.GetAsync("chicago-years");

        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Metadata);
        Assert.AreEqual("1995", result.Metadata["subjectTimeStart"]);
        Assert.AreEqual("2003", result.Metadata["subjectTimeEnd"]);
    }
}

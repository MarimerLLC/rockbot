using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Memory;

namespace RockBot.Memory.Tests;

[TestClass]
public class MemoryToolsTests
{
    // -------------------------------------------------------------------------
    // SearchMemory — age formatting
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task SearchMemory_EntryCreatedToday_ShowsToday()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Test content", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("test");

        StringAssert.Contains(result, "today");
    }

    [TestMethod]
    public async Task SearchMemory_EntryCreatedOneDayAgo_ShowsOneDayAgo()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Test content", DateTimeOffset.UtcNow.AddDays(-1)));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("test");

        StringAssert.Contains(result, "1 day ago");
    }

    [TestMethod]
    public async Task SearchMemory_EntryCreatedMultipleDaysAgo_ShowsNDaysAgo()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Test content", DateTimeOffset.UtcNow.AddDays(-7)));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("test");

        StringAssert.Contains(result, "7 days ago");
    }

    [TestMethod]
    public async Task SearchMemory_OneDayAgeLabel_IsNotPluralised()
    {
        // "1 days ago" would be wrong; must be "1 day ago"
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Test content", DateTimeOffset.UtcNow.AddDays(-1)));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("test");

        Assert.IsFalse(result.Contains("1 days ago"), "Age label should be '1 day ago', not '1 days ago'");
    }

    [TestMethod]
    public async Task SearchMemory_MultipleEntries_EachShowsCorrectAge()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Content A", DateTimeOffset.UtcNow));
        memory.Add(Entry("id2", "Content B", DateTimeOffset.UtcNow.AddDays(-3)));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory();

        StringAssert.Contains(result, "today");
        StringAssert.Contains(result, "3 days ago");
    }

    [TestMethod]
    public async Task SearchMemory_NoResults_ReturnsNoMemoriesFound()
    {
        var tools = MakeTools(new StubLongTermMemory());

        var result = await tools.SearchMemory("anything");

        StringAssert.Contains(result, "No memories found");
    }

    [TestMethod]
    public async Task SearchMemory_EntryId_AppearsInBrackets()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("abc123", "Some fact", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory();

        StringAssert.Contains(result, "[abc123]");
    }

    // -------------------------------------------------------------------------
    // EditMemory
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Tools_ExposeEditMemory()
    {
        var tools = MakeTools(new StubLongTermMemory());

        var names = tools.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();

        CollectionAssert.Contains(names, "edit_memory");
    }

    [TestMethod]
    public async Task EditMemory_AppliesTheEdit_AndReportsTheCount()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("abc123", "User lives in Chicago", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.EditMemory("abc123", "Chicago", "Minneapolis");

        StringAssert.Contains(result, "abc123");
        StringAssert.Contains(result, "replaced 1 occurrence");
        Assert.AreEqual("User lives in Minneapolis", (await memory.GetAsync("abc123"))!.Content);
    }

    [TestMethod]
    public async Task EditMemory_Refusal_ReachesTheModelVerbatim()
    {
        // The store's wording is the only explanation the model gets; paraphrasing it here
        // would strip the part that says how to fix the call.
        const string refusal = "oldText occurs 3 times — the edit target is ambiguous.";
        var memory = new StubLongTermMemory { EditResult = ContentEditResult.Failed(refusal) };
        var tools = MakeTools(memory);

        var result = await tools.EditMemory("abc123", "dogs", "cats");

        StringAssert.Contains(result, refusal);
    }

    [TestMethod]
    public async Task EditMemory_PassesReplaceAllThrough()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("abc123", "dog dog", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.EditMemory("abc123", "dog", "cat", replace_all: true);

        StringAssert.Contains(result, "replaced 2 occurrences");
        Assert.AreEqual("cat cat", (await memory.GetAsync("abc123"))!.Content);
    }

    [TestMethod]
    public async Task EditMemory_WithoutReplaceAll_AmbiguousMatchIsRefused()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("abc123", "dog dog", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.EditMemory("abc123", "dog", "cat");

        StringAssert.Contains(result, "ambiguous");
        Assert.AreEqual("dog dog", (await memory.GetAsync("abc123"))!.Content);
    }

    // -------------------------------------------------------------------------
    // DeleteMemory
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task DeleteMemory_KnownId_ReturnsConfirmationWithContent()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Rocky has a dog named Milo", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.DeleteMemory("id1");

        StringAssert.Contains(result, "id1");
        StringAssert.Contains(result, "Rocky has a dog named Milo");
    }

    [TestMethod]
    public async Task DeleteMemory_KnownId_EntryIsRemoved()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "To be deleted", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        await tools.DeleteMemory("id1");

        Assert.IsNull(await memory.GetAsync("id1"));
    }

    [TestMethod]
    public async Task DeleteMemory_UnknownId_ReturnsNotFoundMessage()
    {
        var tools = MakeTools(new StubLongTermMemory());

        var result = await tools.DeleteMemory("nonexistent");

        StringAssert.Contains(result, "No memory entry found");
    }

    // -------------------------------------------------------------------------
    // UpdateMemoryImportance
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task UpdateMemoryImportance_KnownId_UpdatesScore()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Some fact", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.UpdateMemoryImportance("id1", 0.9f);

        StringAssert.Contains(result, "0.50");
        StringAssert.Contains(result, "0.90");
        var updated = await memory.GetAsync("id1");
        Assert.AreEqual(0.9f, updated!.ImportanceScore);
    }

    [TestMethod]
    public async Task UpdateMemoryImportance_UnknownId_ReturnsNotFound()
    {
        var tools = MakeTools(new StubLongTermMemory());

        var result = await tools.UpdateMemoryImportance("nonexistent", 0.8f);

        StringAssert.Contains(result, "No memory entry found");
    }

    [TestMethod]
    public async Task UpdateMemoryImportance_ClampsAbove1()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Test", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        await tools.UpdateMemoryImportance("id1", 1.5f);

        var updated = await memory.GetAsync("id1");
        Assert.AreEqual(1.0f, updated!.ImportanceScore);
    }

    [TestMethod]
    public async Task UpdateMemoryImportance_ClampsBelow0()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Test", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        await tools.UpdateMemoryImportance("id1", -0.5f);

        var updated = await memory.GetAsync("id1");
        Assert.AreEqual(0.0f, updated!.ImportanceScore);
    }

    [TestMethod]
    public async Task UpdateMemoryImportance_SetsUpdatedAt()
    {
        var memory = new StubLongTermMemory();
        var created = DateTimeOffset.UtcNow.AddDays(-30);
        memory.Add(new MemoryEntry("id1", "Old fact", null, [], created));
        var tools = MakeTools(memory);

        await tools.UpdateMemoryImportance("id1", 0.8f);

        var updated = await memory.GetAsync("id1");
        Assert.IsNotNull(updated!.UpdatedAt);
        Assert.IsTrue(updated.UpdatedAt!.Value > created);
    }

    // -------------------------------------------------------------------------
    // SearchMemory — importance display
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task SearchMemory_ShowsImportanceScore()
    {
        var memory = new StubLongTermMemory();
        memory.Add(new MemoryEntry("id1", "Important fact", null, [], DateTimeOffset.UtcNow, ImportanceScore: 0.85f));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory();

        StringAssert.Contains(result, "importance=0.85");
    }

    // -------------------------------------------------------------------------
    // SearchMemory — mode parameter
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task SearchMemory_DefaultMode_PassesHybrid()
    {
        var memory = new StubLongTermMemory();
        var tools = MakeTools(memory);

        await tools.SearchMemory("anything");

        Assert.IsNotNull(memory.LastCriteria);
        Assert.AreEqual(MemorySearchMode.Hybrid, memory.LastCriteria.Mode);
    }

    [TestMethod]
    public async Task SearchMemory_ModeRegex_PassesRegex()
    {
        var memory = new StubLongTermMemory();
        var tools = MakeTools(memory);

        await tools.SearchMemory("anything", mode: "Regex");

        Assert.IsNotNull(memory.LastCriteria);
        Assert.AreEqual(MemorySearchMode.Regex, memory.LastCriteria.Mode);
    }

    [TestMethod]
    public async Task SearchMemory_ModeRegex_LowerCase_PassesRegex()
    {
        var memory = new StubLongTermMemory();
        var tools = MakeTools(memory);

        await tools.SearchMemory("anything", mode: "regex");

        Assert.IsNotNull(memory.LastCriteria);
        Assert.AreEqual(MemorySearchMode.Regex, memory.LastCriteria.Mode);
    }

    [TestMethod]
    public async Task SearchMemory_UnknownMode_ReturnsErrorString_AndDoesNotCallStore()
    {
        var memory = new StubLongTermMemory();
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("anything", mode: "fuzzy");

        StringAssert.Contains(result, "Unknown search mode");
        Assert.IsNull(memory.LastCriteria, "Store should not be called for an invalid mode.");
    }

    [TestMethod]
    public async Task SearchMemory_MemorySearchException_ReturnsMessage()
    {
        var memory = new StubLongTermMemory
        {
            SearchOverride = _ => throw new MemorySearchException("bad pattern: nope")
        };
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("[", mode: "regex");

        StringAssert.Contains(result, "bad pattern: nope");
    }

    // -------------------------------------------------------------------------
    // SearchMemory — folded-in list_categories taxonomy (issue #484)
    // -------------------------------------------------------------------------

    [TestMethod]
    public void Tools_DoNotExposeListCategories()
    {
        var tools = MakeTools(new StubLongTermMemory());

        var names = tools.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();

        CollectionAssert.DoesNotContain(names, "list_categories",
            "list_categories is folded into the query-less search_memory path and must not be a separate tool.");
        CollectionAssert.Contains(names, "search_memory");
    }

    [TestMethod]
    public async Task SearchMemory_NoQuery_AppendsCategoryTaxonomy()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Some fact", DateTimeOffset.UtcNow));
        memory.Categories.AddRange(["project-context/rockbot", "user-preferences/style"]);
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory();

        StringAssert.Contains(result, "Some fact");
        StringAssert.Contains(result, "Memory categories (2):");
        StringAssert.Contains(result, "- project-context/rockbot");
        StringAssert.Contains(result, "- user-preferences/style");
    }

    [TestMethod]
    public async Task SearchMemory_NoQuery_TaxonomyIsWholeStore_NotJustMatchedCategories()
    {
        // list_categories showed every category, not only those present in the current page.
        // Folding it in must not narrow that: a scoped browse still reveals sibling categories.
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "A preference", DateTimeOffset.UtcNow));
        memory.Categories.AddRange(["user-preferences/style", "project-context/rockbot"]);
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory(category: "user-preferences");

        StringAssert.Contains(result, "- project-context/rockbot",
            "Browsing one category must still surface the full taxonomy for discovery.");
    }

    [TestMethod]
    public async Task SearchMemory_WithQuery_DoesNotAppendCategoryTaxonomy()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Some fact", DateTimeOffset.UtcNow));
        memory.Categories.Add("project-context/rockbot");
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("fact");

        Assert.IsFalse(result.Contains("Memory categories"),
            "A keyword search is not a browse — the taxonomy would just be token noise.");
        Assert.AreEqual(0, memory.ListCategoriesCallCount);
    }

    [TestMethod]
    public async Task SearchMemory_NoQuery_NoResults_StillAppendsCategoryTaxonomy()
    {
        // A category filter that matches nothing is exactly when knowing the real
        // category names is most useful.
        var memory = new StubLongTermMemory { SearchOverride = _ => [] };
        memory.Categories.Add("project-context/rockbot");
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory(category: "no-such-category");

        StringAssert.Contains(result, "No memories found");
        StringAssert.Contains(result, "- project-context/rockbot");
    }

    [TestMethod]
    public async Task SearchMemory_NoQuery_NoCategories_OmitsTaxonomySection()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Some fact", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory();

        Assert.IsFalse(result.Contains("Memory categories"));
    }

    [TestMethod]
    public async Task SearchMemory_NoQuery_TaxonomyFailure_DoesNotFailTheSearch()
    {
        var memory = new StubLongTermMemory { FailListCategories = true };
        memory.Add(Entry("id1", "Some fact", DateTimeOffset.UtcNow));
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory();

        StringAssert.Contains(result, "Some fact");
        Assert.IsFalse(result.Contains("Memory categories"));
    }

    [TestMethod]
    public async Task SearchMemory_Category_IsPassedToStoreAsFilter()
    {
        var memory = new StubLongTermMemory();
        var tools = MakeTools(memory);

        await tools.SearchMemory("timezone", category: "user-preferences");

        Assert.AreEqual("user-preferences", memory.LastCriteria?.Category);
        Assert.AreEqual("timezone", memory.LastCriteria?.Query);
    }

    [TestMethod]
    public async Task SearchMemory_WhitespaceQuery_IsTreatedAsBrowse()
    {
        var memory = new StubLongTermMemory();
        memory.Add(Entry("id1", "Some fact", DateTimeOffset.UtcNow));
        memory.Categories.Add("project-context/rockbot");
        var tools = MakeTools(memory);

        var result = await tools.SearchMemory("   ");

        Assert.IsNull(memory.LastCriteria?.Query);
        StringAssert.Contains(result, "Memory categories");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    // ── Recall-family empty results ───────────────────────────────────────

    [TestMethod]
    public async Task SearchMemory_NoResults_PointsAtTheSiblingRecallTool()
    {
        // Durable memory is one of two recall stores. A query that finds nothing here is not
        // evidence the fact was never known — it may have been returned by a tool earlier
        // this session and cached in working memory.
        var tools = MakeTools(new StubLongTermMemory());

        var result = await tools.SearchMemory("anything");

        StringAssert.Contains(result, RecallTools.WorkingMemory);
        Assert.IsFalse(result.Contains($"use {RecallTools.DurableMemory}"),
            "Re-suggesting the tool that just came back empty invites a retry loop.");
    }

    [TestMethod]
    public async Task SearchMemory_EmptyBrowse_DoesNotPointElsewhere()
    {
        // A query-less call is "how is knowledge organised here?", not a failed lookup. It
        // already answers itself with the category taxonomy; sibling pointers would be noise.
        var tools = MakeTools(new StubLongTermMemory());

        var result = await tools.SearchMemory();

        Assert.IsFalse(result.Contains(RecallTools.WorkingMemory));
    }

    private static MemoryTools MakeTools(StubLongTermMemory memory) =>
        new(memory, new StubChatClient(), Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()), NullLogger<MemoryTools>.Instance);

    private static MemoryEntry Entry(string id, string content, DateTimeOffset createdAt) =>
        new(id, content, null, [], createdAt);

    // -------------------------------------------------------------------------
    // SaveMemory — observation soft gate (Phase 2)
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task SaveMemory_ClaimLanguage_AppendsObservationHintToResult()
    {
        var memory = new StubLongTermMemory();
        var tools = MakeTools(memory);

        var result = await tools.SaveMemory("calendar wrapper cannot pass arguments");

        StringAssert.Contains(result, "looks like a capability claim");
    }

    [TestMethod]
    public async Task SaveMemory_BenignContent_ReturnsBareQueuedMessage()
    {
        var memory = new StubLongTermMemory();
        var tools = MakeTools(memory);

        var result = await tools.SaveMemory("user prefers concise updates");

        Assert.IsFalse(result.Contains("capability claim"),
            "Benign content must not produce a soft-gate hint.");
    }

    // -------------------------------------------------------------------------
    // SaveMemory — Phase 3 scoped-category direct save
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task SaveMemory_FeedbackCategory_BypassesLlmExtraction_AndPreservesCategoryVerbatim()
    {
        var memory = new StubLongTermMemory();
        var llm = new StubChatClient();
        var tools = MakeToolsWith(memory, llm);

        await tools.SaveMemory(
            "Always include a TL;DR section at the top of status reports",
            category: "feedback/from-agent/status-reports",
            tags: "style,reports");

        var saved = await WaitForSavedAsync(memory, expected: 1);
        Assert.AreEqual(0, llm.CallCount,
            "Scoped feedback saves must bypass the LLM extraction pass entirely.");
        Assert.AreEqual("feedback/from-agent/status-reports", saved[0].Category,
            "Caller-supplied scoped category must be preserved verbatim.");
        Assert.AreEqual(
            "Always include a TL;DR section at the top of status reports",
            saved[0].Content,
            "Scoped saves must persist content verbatim, no LLM rewriting.");
        CollectionAssert.AreEquivalent(new[] { "style", "reports" }, saved[0].Tags.ToArray());
    }

    [TestMethod]
    public async Task SaveMemory_CapabilityClaimCategory_BypassesLlmExtraction()
    {
        var memory = new StubLongTermMemory();
        var llm = new StubChatClient();
        var tools = MakeToolsWith(memory, llm);

        await tools.SaveMemory(
            "wrapper cannot pass arguments",
            category: "claim/capability/calendar-mcp/get_calendar_events");

        var saved = await WaitForSavedAsync(memory, expected: 1);
        Assert.AreEqual(0, llm.CallCount);
        Assert.AreEqual("claim/capability/calendar-mcp/get_calendar_events", saved[0].Category);
    }

    [TestMethod]
    public async Task SaveMemory_FeedbackCategory_OppositeDirective_SupersedesEarlierEntry()
    {
        var memory = new StubLongTermMemory();
        // Test the wiring contract: a detector returning NewerWins drives the supersession path.
        // The real keyword detector behaviour is covered by MemoryContradictionDetectorTests.
        var detector = new FakeContradictionDetector(memory);
        var tools = MakeToolsWith(memory, new StubChatClient(), detector);

        await tools.SaveMemory(
            "Always include a TL;DR section at the top of status reports",
            category: "feedback/from-agent/status-reports");
        await WaitForSavedAsync(memory, expected: 1);

        await tools.SaveMemory(
            "Never include a TL;DR section in status reports — they should be concise without one",
            category: "feedback/from-agent/status-reports");
        await WaitForSavedAsync(memory, expected: 2);

        // Both entries land on disk; the older one carries SupersededBy after the
        // contradiction detector runs.
        var entries = memory.SnapshotAll();
        Assert.AreEqual(2, entries.Count);
        var loser = entries.Single(e => e.Content.Contains("Always"));
        var winner = entries.Single(e => e.Content.Contains("Never"));
        Assert.AreEqual(winner.Id, loser.SupersededBy,
            "Older entry in the same scoped category should be marked superseded.");
        Assert.IsNull(winner.SupersededBy);
    }

    /// <summary>
    /// Test detector that supersedes any prior live entry sharing the incoming entry's
    /// category. Validates the MemoryTools wiring without coupling to the real keyword
    /// detector's heuristics.
    /// </summary>
    private sealed class FakeContradictionDetector : IMemoryContradictionDetector
    {
        private readonly StubLongTermMemory _memory;
        public FakeContradictionDetector(StubLongTermMemory memory) => _memory = memory;

        public Task<ContradictionResolution> ResolveAsync(MemoryEntry incoming, CancellationToken cancellationToken = default)
        {
            var existing = _memory.SnapshotAll()
                .Where(e => e.Id != incoming.Id
                    && e.SupersededBy is null
                    && string.Equals(e.Category, incoming.Category, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Id)
                .ToList();
            return Task.FromResult(existing.Count > 0
                ? ContradictionResolution.NewerWins(existing)
                : ContradictionResolution.None);
        }
    }

    [TestMethod]
    public async Task SaveMemory_NonScopedCategory_StillUsesLlmExtractionPath()
    {
        var memory = new StubLongTermMemory();
        var llm = new StubChatClient();
        var tools = MakeToolsWith(memory, llm);

        await tools.SaveMemory(
            "Loves dogs and lives in Minneapolis",
            category: "user-preferences/pets");

        // Wait for the LLM call (regression: the existing extractor path must remain wired up).
        for (var i = 0; i < 50 && llm.CallCount == 0; i++)
            await Task.Delay(20);

        Assert.AreEqual(1, llm.CallCount,
            "Non-scoped categories should still go through the LLM extraction pass.");
    }

    private static MemoryTools MakeToolsWith(
        StubLongTermMemory memory,
        StubChatClient llm,
        IMemoryContradictionDetector? detector = null) =>
        new(
            memory,
            llm,
            Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions()),
            NullLogger<MemoryTools>.Instance,
            detector);

    private static async Task<IReadOnlyList<MemoryEntry>> WaitForSavedAsync(
        StubLongTermMemory memory, int expected, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = memory.SnapshotAll();
            if (snapshot.Count >= expected) return snapshot;
            await Task.Delay(20);
        }
        Assert.Fail($"Timed out waiting for {expected} saved entry/entries; got {memory.SnapshotAll().Count}.");
        return [];
    }
}

// ---------------------------------------------------------------------------
// Stubs
// ---------------------------------------------------------------------------

/// <summary>
/// In-memory implementation of <see cref="ILongTermMemory"/> for tests.
/// <see cref="SearchAsync"/> returns all stored entries regardless of criteria
/// so tests can focus on output formatting rather than search logic.
/// </summary>
internal sealed class StubLongTermMemory : ILongTermMemory
{
    private readonly List<MemoryEntry> _entries = [];

    public MemorySearchCriteria? LastCriteria { get; private set; }
    public Func<MemorySearchCriteria, IReadOnlyList<MemoryEntry>>? SearchOverride { get; set; }

    /// <summary>Categories returned by <see cref="ListCategoriesAsync"/>; empty by default.</summary>
    public List<string> Categories { get; } = [];

    /// <summary>Set to throw from <see cref="ListCategoriesAsync"/>, exercising the taxonomy failure path.</summary>
    public bool FailListCategories { get; set; }

    public int ListCategoriesCallCount { get; private set; }

    public void Add(MemoryEntry entry) => _entries.Add(entry);

    public IReadOnlyList<MemoryEntry> SnapshotAll()
    {
        lock (_entries)
            return _entries.ToList();
    }

    public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_entries)
        {
            _entries.RemoveAll(e => e.Id == entry.Id);
            _entries.Add(entry);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        MemorySearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        LastCriteria = criteria;
        var results = SearchOverride is not null ? SearchOverride(criteria) : (IReadOnlyList<MemoryEntry>)[.. _entries];
        return Task.FromResult(results);
    }

    public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

    /// <summary>When set, <see cref="EditAsync"/> returns this instead of applying the edit.</summary>
    public ContentEditResult? EditResult { get; set; }

    public Task<ContentEditResult> EditAsync(
        string id, string oldText, string newText, bool replaceAll = false,
        CancellationToken cancellationToken = default)
    {
        if (EditResult is { } canned)
            return Task.FromResult(canned);

        lock (_entries)
        {
            var index = _entries.FindIndex(e => e.Id == id);
            if (index < 0)
                return Task.FromResult(ContentEditResult.Failed($"No memory entry found with id '{id}'."));

            var existing = _entries[index];
            var edit = TextEdit.Apply(existing.Content, oldText, newText, replaceAll);
            if (!edit.IsSuccess)
                return Task.FromResult(ContentEditResult.Failed(edit.Error!));

            _entries[index] = existing with { Content = edit.Content!, UpdatedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(ContentEditResult.Applied(
                edit.ReplacementCount, existing.Content.Length, edit.Content!.Length));
        }
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _entries.RemoveAll(e => e.Id == id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default)
    {
        ListCategoriesCallCount++;
        if (FailListCategories)
            throw new InvalidOperationException("category index unavailable");
        return Task.FromResult<IReadOnlyList<string>>([.. Categories]);
    }
}

/// <summary>
/// Minimal <see cref="ILlmClient"/> stub. Returns an empty JSON array by default
/// so that <see cref="MemoryTools.SaveMemory"/> falls back to direct save gracefully.
/// Not called by SearchMemory or DeleteMemory.
/// </summary>
internal sealed class StubChatClient : ILlmClient
{
    private int _callCount;

    public bool IsIdle => true;
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ModelTier tier,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetResponseAsync(messages, options, cancellationToken);
}

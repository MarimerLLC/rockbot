using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Memory;

namespace RockBot.Memory.Tests;

[TestClass]
public class WorkingMemoryToolsTests
{
    private const string Namespace = "subagent/abc123";
    private StubWorkingMemory _memory = null!;
    private WorkingMemoryTools _tools = null!;

    [TestInitialize]
    public void Setup()
    {
        _memory = new StubWorkingMemory();
        _tools = new WorkingMemoryTools(_memory, Namespace, NullLogger.Instance);
    }

    // ── SaveToWorkingMemory ───────────────────────────────────────────────

    [TestMethod]
    public async Task SaveToWorkingMemory_PlainKey_PrependsNamespace()
    {
        await _tools.SaveToWorkingMemory("my_results", "data");

        Assert.IsTrue(_memory.Store.ContainsKey("subagent/abc123/my_results"));
    }

    [TestMethod]
    public async Task SaveToWorkingMemory_AbsoluteKey_DoesNotDoublePrefix()
    {
        await _tools.SaveToWorkingMemory("subagent/abc123/my_results", "data");

        Assert.IsTrue(_memory.Store.ContainsKey("subagent/abc123/my_results"),
            "Key should be used as-is when it contains '/'");
        Assert.IsFalse(_memory.Store.ContainsKey("subagent/abc123/subagent/abc123/my_results"),
            "Namespace must not be prepended twice");
    }

    // ── EditWorkingMemory ─────────────────────────────────────────────────

    [TestMethod]
    public void Tools_ExposeEditWorkingMemory()
    {
        var names = _tools.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();

        CollectionAssert.Contains(names, "EditWorkingMemory");
    }

    [TestMethod]
    public async Task EditWorkingMemory_PlainKey_PrependsNamespace()
    {
        _memory.Store["subagent/abc123/draft"] = "before";

        await _tools.EditWorkingMemory("draft", "before", "after");

        Assert.AreEqual("subagent/abc123/draft", _memory.LastEditKey);
        Assert.AreEqual("after", _memory.Store["subagent/abc123/draft"]);
    }

    [TestMethod]
    public async Task EditWorkingMemory_AbsoluteKey_UsesAsIs()
    {
        _memory.Store["shared/drafts/report"] = "before";

        await _tools.EditWorkingMemory("shared/drafts/report", "before", "after");

        Assert.AreEqual("shared/drafts/report", _memory.LastEditKey);
    }

    [TestMethod]
    public async Task EditWorkingMemory_Success_ReportsReplacementCount()
    {
        _memory.Store["subagent/abc123/draft"] = "todo todo";

        var result = await _tools.EditWorkingMemory("draft", "todo", "done", replace_all: true);

        StringAssert.Contains(result, "replaced 2 occurrences");
        Assert.AreEqual("done done", _memory.Store["subagent/abc123/draft"]);
    }

    [TestMethod]
    public async Task EditWorkingMemory_Refusal_ReachesTheModelVerbatim()
    {
        const string refusal = "Working memory entry 'x' was not found or has expired.";
        _memory.EditResult = ContentEditResult.Failed(refusal);

        var result = await _tools.EditWorkingMemory("draft", "a", "b");

        StringAssert.Contains(result, refusal);
    }

    // ── GetFromWorkingMemory ──────────────────────────────────────────────

    [TestMethod]
    public async Task GetFromWorkingMemory_PlainKey_PrependsNamespace()
    {
        _memory.Store["subagent/abc123/cached"] = "value";

        var result = await _tools.GetFromWorkingMemory("cached");

        Assert.AreEqual("value", result);
    }

    [TestMethod]
    public async Task GetFromWorkingMemory_AbsoluteKey_UsesAsIs()
    {
        _memory.Store["patrol/heartbeat/alert"] = "alert-data";

        var result = await _tools.GetFromWorkingMemory("patrol/heartbeat/alert");

        Assert.AreEqual("alert-data", result);
    }

    // ── DeleteFromWorkingMemory ───────────────────────────────────────────

    [TestMethod]
    public async Task DeleteFromWorkingMemory_PlainKey_PrependsNamespace()
    {
        _memory.Store["subagent/abc123/old"] = "stale";

        await _tools.DeleteFromWorkingMemory("old");

        Assert.IsFalse(_memory.Store.ContainsKey("subagent/abc123/old"));
    }

    [TestMethod]
    public async Task DeleteFromWorkingMemory_AbsoluteKey_UsesAsIs()
    {
        _memory.Store["patrol/heartbeat/alert"] = "alert-data";

        await _tools.DeleteFromWorkingMemory("patrol/heartbeat/alert");

        Assert.IsFalse(_memory.Store.ContainsKey("patrol/heartbeat/alert"));
    }

    // ── SearchWorkingMemory — folded-in list_working_memory (issue #484) ───

    [TestMethod]
    public void Tools_DoNotExposeListWorkingMemory()
    {
        var names = _tools.Tools.OfType<AIFunction>().Select(f => f.Name).ToList();

        CollectionAssert.DoesNotContain(names, "ListWorkingMemory",
            "list_working_memory is folded into the query-less search_working_memory path.");
        CollectionAssert.Contains(names, "SearchWorkingMemory");
    }

    [TestMethod]
    public async Task SearchWorkingMemory_NoQuery_UsesListingHeaderAndOmitsContentPreview()
    {
        _memory.SearchResults =
        [
            Entry("subagent/abc123/results", "PAYLOADBODY" + new string('z', 500), category: "research", tags: ["urgent"])
        ];

        var result = await _tools.SearchWorkingMemory();

        StringAssert.Contains(result, "Working memory 'subagent/abc123' (1 entries):");
        StringAssert.Contains(result, "- subagent/abc123/results (expires in ");
        StringAssert.Contains(result, "category: research");
        StringAssert.Contains(result, "tags: urgent");
        Assert.IsFalse(result.Contains("PAYLOADBODY"),
            "A listing must not include a content preview — that is the search rendering.");
    }

    [TestMethod]
    public async Task SearchWorkingMemory_WithQuery_KeepsContentPreview()
    {
        _memory.SearchResults = [Entry("subagent/abc123/results", new string('z', 500))];

        var result = await _tools.SearchWorkingMemory("results");

        StringAssert.Contains(result, "Working memory search (query='results')");
        StringAssert.Contains(result, new string('z', 120) + "…");
    }

    [TestMethod]
    public async Task SearchWorkingMemory_NoQuery_RaisesResultCapAboveRankedSearchDefault()
    {
        // list_working_memory enumerated without a cap; the ranked-search default of 20 would
        // have silently truncated a namespace listing after the fold-in.
        await _tools.SearchWorkingMemory();

        Assert.IsNotNull(_memory.LastCriteria);
        Assert.IsTrue(_memory.LastCriteria!.MaxResults >= 500,
            $"Listing cap should be well above the ranked-search default; was {_memory.LastCriteria.MaxResults}.");
    }

    [TestMethod]
    public async Task SearchWorkingMemory_WithQuery_KeepsRankedSearchResultCap()
    {
        await _tools.SearchWorkingMemory("anything");

        Assert.AreEqual(new MemorySearchCriteria().MaxResults, _memory.LastCriteria?.MaxResults);
    }

    [TestMethod]
    public async Task SearchWorkingMemory_NoQuery_NamespaceParam_BrowsesThatPrefix()
    {
        await _tools.SearchWorkingMemory(@namespace: "patrol");

        Assert.AreEqual("patrol", _memory.LastPrefix);
        Assert.IsNull(_memory.LastCriteria?.Query);
    }

    [TestMethod]
    public async Task SearchWorkingMemory_NoQuery_OwnNamespaceEmpty_ReturnsEmptyWording()
    {
        var result = await _tools.SearchWorkingMemory();

        Assert.AreEqual("Working memory is empty.", result);
    }

    [TestMethod]
    public async Task SearchWorkingMemory_NoQuery_OtherNamespaceEmpty_ReturnsNoEntriesWording()
    {
        var result = await _tools.SearchWorkingMemory(@namespace: "patrol");

        Assert.AreEqual("No entries found in namespace 'patrol'.", result);
    }

    [TestMethod]
    public async Task SearchWorkingMemory_NoQueryButFiltered_NoMatch_UsesSearchWording()
    {
        // Filters make it a search that found nothing, not an empty namespace.
        var result = await _tools.SearchWorkingMemory(category: "research");

        StringAssert.Contains(result, "No working memory entries matched (category='research')");
    }

    [TestMethod]
    public async Task SearchWorkingMemory_WhitespaceQuery_IsTreatedAsListing()
    {
        _memory.SearchResults = [Entry("subagent/abc123/results", "PAYLOADBODY" + new string('z', 500))];

        var result = await _tools.SearchWorkingMemory("   ");

        Assert.IsNull(_memory.LastCriteria?.Query);
        StringAssert.Contains(result, "Working memory 'subagent/abc123' (1 entries):");
        Assert.IsFalse(result.Contains("PAYLOADBODY"));
    }

    [TestMethod]
    public async Task SearchWorkingMemory_CategoryAndTags_ArePassedToStore()
    {
        await _tools.SearchWorkingMemory("alerts", category: "patrol-finding", tags: "urgent, inbox");

        Assert.AreEqual("patrol-finding", _memory.LastCriteria?.Category);
        CollectionAssert.AreEqual(new[] { "urgent", "inbox" }, _memory.LastCriteria?.Tags?.ToArray());
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // ── stash namespace alias ─────────────────────────────────────────────
    //
    // The model cannot learn its own namespace, so bare "stash" has to resolve to this
    // context's stash (stash/{namespace}) or the only reachable prefix would be the shared
    // "stash" root holding every context's elided tool results.

    [TestMethod]
    public async Task SearchWorkingMemory_BareStashNamespace_ExpandsToOwnStashPrefix()
    {
        await _tools.SearchWorkingMemory(query: "invoice", @namespace: "stash");

        Assert.AreEqual("stash/subagent/abc123", _memory.LastPrefix);
    }

    [TestMethod]
    public async Task SearchWorkingMemory_StashWithTrailingSlash_ExpandsToOwnStashPrefix()
    {
        await _tools.SearchWorkingMemory(query: "invoice", @namespace: "stash/");

        Assert.AreEqual("stash/subagent/abc123", _memory.LastPrefix);
    }

    [TestMethod]
    public async Task SearchWorkingMemory_BareStashNamespace_IsCaseInsensitive()
    {
        await _tools.SearchWorkingMemory(query: "invoice", @namespace: "STASH");

        Assert.AreEqual("stash/subagent/abc123", _memory.LastPrefix);
    }

    [TestMethod]
    public async Task SearchWorkingMemory_ExplicitStashPath_PassesThroughUnchanged()
    {
        await _tools.SearchWorkingMemory(query: "invoice", @namespace: "stash/session/other");

        Assert.AreEqual("stash/session/other", _memory.LastPrefix,
            "An explicit cross-context stash path must not be rewritten to the caller's own stash");
    }

    // ── Recall-family empty results ───────────────────────────────────────

    [TestMethod]
    public async Task SearchWorkingMemory_QueryMatchesNothing_PointsAtTheSiblingRecallTool()
    {
        // Working memory is one of two recall stores. Nothing cached this session does not mean
        // nothing known — the fact may have been concluded and saved durably instead.
        var result = await _tools.SearchWorkingMemory(query: "nothing-matches-this");

        StringAssert.Contains(result, RecallTools.DurableMemory);
        Assert.IsFalse(result.Contains($"use {RecallTools.WorkingMemory}"),
            "Re-suggesting the tool that just came back empty invites a retry loop.");
    }

    private static WorkingMemoryEntry Entry(
        string key, string value, string? category = null, IReadOnlyList<string>? tags = null) =>
        new(key, value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), category, tags);

    // ── Stub ──────────────────────────────────────────────────────────────

    private sealed class StubWorkingMemory : IWorkingMemory
    {
        public Dictionary<string, string> Store { get; } = new();

        /// <summary>Entries returned verbatim by <see cref="SearchAsync"/>, so tests exercise rendering.</summary>
        public IReadOnlyList<WorkingMemoryEntry> SearchResults { get; set; } = [];

        public MemorySearchCriteria? LastCriteria { get; private set; }
        public string? LastPrefix { get; private set; }

        public Task SetAsync(string key, string value, TimeSpan? ttl = null,
            string? category = null, IReadOnlyList<string>? tags = null)
        {
            Store[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(Store.TryGetValue(key, out var v) ? v : null);

        /// <summary>When set, <see cref="EditAsync"/> returns this instead of applying the edit.</summary>
        public ContentEditResult? EditResult { get; set; }

        public string? LastEditKey { get; private set; }

        public Task<ContentEditResult> EditAsync(string key, string oldText, string newText, bool replaceAll = false)
        {
            LastEditKey = key;

            if (EditResult is { } canned)
                return Task.FromResult(canned);

            if (!Store.TryGetValue(key, out var current))
                return Task.FromResult(ContentEditResult.Failed($"Working memory entry '{key}' was not found."));

            var edit = TextEdit.Apply(current, oldText, newText, replaceAll);
            if (!edit.IsSuccess)
                return Task.FromResult(ContentEditResult.Failed(edit.Error!));

            Store[key] = edit.Content!;
            return Task.FromResult(ContentEditResult.Applied(
                edit.ReplacementCount, current.Length, edit.Content!.Length));
        }

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

        public Task DeleteAsync(string key)
        {
            Store.Remove(key);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string? prefix = null)
        {
            Store.Clear();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null)
        {
            LastCriteria = criteria;
            LastPrefix = prefix;
            return Task.FromResult(SearchResults);
        }
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>#418: finished work is saved without the model deciding to, and the agent can find it again.</summary>
[TestClass]
public class ResultArchiveTests
{
    private static ResultArchive Archive(ISavedResponseStore store, SavedResponseOptions? options = null) =>
        new(store, Options.Create(options ?? new SavedResponseOptions()), NullLogger<ResultArchive>.Instance);

    private static readonly string LongResult = "# Vendor comparison\n\n" + string.Join("\n", Enumerable.Repeat("A finding line.", 80));

    // ── ResultArchive ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Archive_SavesBackgroundResult_AsAutomatic()
    {
        var store = new ListStore();

        var id = await Archive(store).ArchiveAsync(
            SavedResponseSources.Subagent, "Compare the three vendors", LongResult, "RockBot", "s1");

        var saved = store.Items.Single();
        Assert.AreEqual(id, saved.Id);
        Assert.AreEqual("Compare the three vendors", saved.Label);
        Assert.AreEqual(SavedResponseSources.Subagent, saved.Source);
        Assert.IsTrue(saved.AutoSaved);
        Assert.AreEqual("s1", saved.SessionId);
    }

    [TestMethod]
    public async Task Archive_SkipsShortStatusLines()
    {
        var store = new ListStore();

        var id = await Archive(store).ArchiveAsync(SavedResponseSources.Scheduled, "patrol", "All clear.", "RockBot", "s1");

        Assert.IsNull(id);
        Assert.AreEqual(0, store.Items.Count);
    }

    [TestMethod]
    public async Task ArchiveReply_RequiresToolUseAndLength()
    {
        var store = new ListStore();
        var archive = Archive(store);

        Assert.IsNull(await archive.ArchiveReplyAsync("hi", LongResult, toolCallCount: 0, "RockBot", "s1"),
            "A long answer with no tool work is conversation");
        Assert.IsNull(await archive.ArchiveReplyAsync("check x", "Done — it's fine.", toolCallCount: 4, "RockBot", "s1"),
            "A short answer is not a result worth keeping");
        Assert.IsNotNull(await archive.ArchiveReplyAsync("research x", LongResult, toolCallCount: 4, "RockBot", "s1"));

        Assert.AreEqual(SavedResponseSources.Reply, store.Items.Single().Source);
    }

    [TestMethod]
    public async Task Archive_NeverThrows_WhenTheStoreFails()
    {
        var id = await Archive(new ThrowingStore()).ArchiveAsync(
            SavedResponseSources.A2A, "label", LongResult, "RockBot", "s1");

        Assert.IsNull(id);
    }

    [TestMethod]
    public void BuildLabel_FallsBackToFirstContentLine_AndCapsLength()
    {
        Assert.AreEqual("Vendor comparison", ResultArchive.BuildLabel(null, LongResult));
        Assert.AreEqual("Vendor comparison", ResultArchive.BuildLabel("   ", "\n\n**Vendor comparison**\nbody"));
        Assert.AreEqual("Two lines", ResultArchive.BuildLabel("Two   lines\nsecond", "x"));
        Assert.AreEqual("Agent result", ResultArchive.BuildLabel(null, "  "));

        var longLabel = ResultArchive.BuildLabel(new string('L', 300), "x");
        Assert.AreEqual(ResultArchive.MaxLabelChars, longLabel.Length);
        Assert.IsTrue(longLabel.EndsWith('…'));
    }

    // ── SavedResponseTools ───────────────────────────────────────────────────

    private static SavedResponseTools Tools(ISavedResponseStore store) =>
        new(store, "s1", "RockBot", NullLogger.Instance);

    [TestMethod]
    public async Task List_FiltersByEveryQueryWord_AcrossLabelAndContent()
    {
        var store = new ListStore();
        await store.SaveAsync(new SavedResponse("a", "Vendor comparison", "pricing for Acme and Globex", "RockBot", "s", DateTimeOffset.UtcNow, SavedResponseSources.Subagent, true));
        await store.SaveAsync(new SavedResponse("b", "Weekly briefing", "calendar", "RockBot", "s", DateTimeOffset.UtcNow.AddMinutes(-1), SavedResponseSources.Scheduled, true));

        var result = await Tools(store).ListSavedResponses("vendor acme");

        StringAssert.Contains(result, "id=a");
        StringAssert.Contains(result, "subagent");
        Assert.IsFalse(result.Contains("id=b"));
        StringAssert.Contains(await Tools(store).ListSavedResponses("nothing-matches"), "No saved responses match");
    }

    [TestMethod]
    public async Task Get_ReturnsContent_OrPointsAtList()
    {
        var store = new ListStore();
        await store.SaveAsync(new SavedResponse("a", "Vendor comparison", "the full report", "RockBot", "s", DateTimeOffset.UtcNow));

        var tools = Tools(store);
        StringAssert.Contains(await tools.GetSavedResponse("missing"), "list_saved_responses");
        Assert.IsFalse(tools.ServedSavedContent, "A miss served nothing");

        StringAssert.Contains(await tools.GetSavedResponse("a"), "the full report");
        Assert.IsTrue(tools.ServedSavedContent, "The turn's reply now hands back a saved result");
    }

    [TestMethod]
    public async Task Save_IsAnExplicitAgentSave()
    {
        var store = new ListStore();

        var result = await Tools(store).SaveResponse("Trip plan", "Day 1: ...");

        var saved = store.Items.Single();
        Assert.AreEqual(SavedResponseSources.Agent, saved.Source);
        Assert.IsFalse(saved.AutoSaved, "Explicit saves are exempt from the automatic age window");
        Assert.AreEqual("s1", saved.SessionId);
        StringAssert.Contains(result, saved.Id);
    }

    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class ListStore : ISavedResponseStore
    {
        public List<SavedResponse> Items { get; } = [];

        public Task SaveAsync(SavedResponse response, CancellationToken cancellationToken = default)
        {
            Items.Add(response);
            return Task.CompletedTask;
        }

        public Task<SavedResponse?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<SavedResponse>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SavedResponse>>(Items.OrderByDescending(r => r.SavedAt).ToList());

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(r => r.Id == id);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingStore : ISavedResponseStore
    {
        public Task SaveAsync(SavedResponse response, CancellationToken cancellationToken = default) =>
            throw new IOException("disk full");
        public Task<SavedResponse?> GetAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SavedResponse>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

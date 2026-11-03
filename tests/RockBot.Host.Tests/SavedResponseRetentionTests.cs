using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>#418: the saved-response store bounds itself — content size, age, per-task runs, counts.</summary>
[TestClass]
public class SavedResponseRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rockbot-saved-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static SavedResponse Entry(
        string id, TimeSpan age, bool auto = true, string? source = SavedResponseSources.Subagent,
        string label = "label", string content = "content") =>
        new(id, label, content, "agent", "s1", Now - age, source, auto);

    private static List<string> Pruned(IEnumerable<SavedResponse> entries, SavedResponseOptions options) =>
        FileSavedResponseStore.SelectForPruning(entries, options, Now).Select(r => r.Id).Order().ToList();

    private FileSavedResponseStore Store(SavedResponseOptions options, TimeProvider? time = null)
    {
        options.BasePath = _dir;
        return new FileSavedResponseStore(
            Options.Create(options), Options.Create(new AgentProfileOptions()),
            NullLogger<FileSavedResponseStore>.Instance, time ?? new FixedTime(Now));
    }

    // ── selection rules ──────────────────────────────────────────────────────

    [TestMethod]
    public void AutoSaved_OlderThanRetention_IsPruned_ExplicitIsNot()
    {
        var entries = new[]
        {
            Entry("old-auto", TimeSpan.FromDays(31)),
            Entry("new-auto", TimeSpan.FromDays(29)),
            Entry("old-user", TimeSpan.FromDays(400), auto: false, source: SavedResponseSources.User),
        };

        CollectionAssert.AreEqual(new[] { "old-auto" },
            Pruned(entries, new SavedResponseOptions { AutoSavedRetention = TimeSpan.FromDays(30) }));
    }

    [TestMethod]
    public void ScheduledReports_KeepOnlyLatestRunsPerTask()
    {
        var entries = Enumerable.Range(1, 5)
            .Select(i => Entry($"brief-{i}", TimeSpan.FromDays(i), source: SavedResponseSources.Scheduled, label: "morning-briefing"))
            .Append(Entry("other-task", TimeSpan.FromDays(10), source: SavedResponseSources.Scheduled, label: "evening-summary"))
            .ToList();

        CollectionAssert.AreEqual(new[] { "brief-4", "brief-5" },
            Pruned(entries, new SavedResponseOptions { MaxScheduledRunsPerTask = 3 }));
    }

    [TestMethod]
    public void NonScheduledResults_SharingALabel_DoNotPruneEachOther()
    {
        var entries = Enumerable.Range(1, 6)
            .Select(i => Entry($"mcp-{i}", TimeSpan.FromHours(i), source: SavedResponseSources.BackgroundTask, label: "research/deep_research result"))
            .ToList();

        Assert.AreEqual(0, Pruned(entries, new SavedResponseOptions { MaxScheduledRunsPerTask = 3 }).Count);
    }

    [TestMethod]
    public void AutoSaved_PastTotalCap_OldestArePruned()
    {
        var entries = Enumerable.Range(1, 5)
            .Select(i => Entry($"a{i}", TimeSpan.FromHours(i), label: $"job {i}"))
            .ToList();

        CollectionAssert.AreEqual(new[] { "a4", "a5" },
            Pruned(entries, new SavedResponseOptions { MaxAutoSaved = 3 }));
    }

    [TestMethod]
    public void Explicit_PastCap_OldestArePruned_AndAutoCapIsSeparate()
    {
        var entries = Enumerable.Range(1, 4)
            .Select(i => Entry($"u{i}", TimeSpan.FromDays(i), auto: false, source: SavedResponseSources.User))
            .Concat(Enumerable.Range(1, 2).Select(i => Entry($"a{i}", TimeSpan.FromHours(i), label: $"job {i}")))
            .ToList();

        CollectionAssert.AreEqual(new[] { "u3", "u4" },
            Pruned(entries, new SavedResponseOptions { MaxExplicitSaved = 2, MaxAutoSaved = 5 }));
    }

    [TestMethod]
    public void CapContent_CutsLongContentWithANote()
    {
        var capped = FileSavedResponseStore.CapContent(Entry("x", TimeSpan.Zero, content: new string('a', 50)), 10);

        StringAssert.StartsWith(capped.Content, new string('a', 10) + "\n\n[Truncated:");
        StringAssert.Contains(capped.Content, "50 characters");

        var shortEntry = Entry("y", TimeSpan.Zero, content: "short");
        Assert.AreSame(shortEntry, FileSavedResponseStore.CapContent(shortEntry, 10));
    }

    // ── the store applies the rules ──────────────────────────────────────────

    [TestMethod]
    public async Task Save_PrunesPastLimits_FromIndexAndDisk()
    {
        var store = Store(new SavedResponseOptions { MaxAutoSaved = 2 });

        for (var i = 1; i <= 4; i++)
            await store.SaveAsync(Entry($"a{i}", TimeSpan.FromMinutes(10 - i), label: $"job {i}"));

        var ids = (await store.ListAsync()).Select(r => r.Id).ToList();
        CollectionAssert.AreEqual(new[] { "a4", "a3" }, ids);
        CollectionAssert.AreEquivalent(new[] { "a3.json", "a4.json" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task Load_PrunesEntriesThatAgedOutWhileStopped()
    {
        var store = Store(new SavedResponseOptions());
        await store.SaveAsync(Entry("fresh", TimeSpan.FromDays(1)));
        await store.SaveAsync(Entry("stale", TimeSpan.FromDays(1)));

        // Pretend 'stale' was written long ago, then restart the store.
        var stalePath = Path.Combine(_dir, "stale.json");
        var stale = Entry("stale", TimeSpan.FromDays(90));
        await File.WriteAllTextAsync(stalePath, JsonSerializer.Serialize(stale,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var restarted = Store(new SavedResponseOptions());
        var ids = (await restarted.ListAsync()).Select(r => r.Id).ToList();

        CollectionAssert.AreEqual(new[] { "fresh" }, ids);
        Assert.IsFalse(File.Exists(stalePath));
    }

    [TestMethod]
    public async Task Save_CapsContent()
    {
        var store = Store(new SavedResponseOptions { MaxContentChars = 20 });

        await store.SaveAsync(Entry("big", TimeSpan.Zero, content: new string('z', 1000)));

        var saved = await store.GetAsync("big");
        Assert.IsNotNull(saved);
        Assert.IsTrue(saved.Content.Length < 200);
    }

    [TestMethod]
    public async Task EntryWrittenBeforeSourceExisted_LoadsAsExplicitUserSave()
    {
        // The pre-#418 file shape: no source, no autoSaved.
        await File.WriteAllTextAsync(Path.Combine(_dir, "legacy.json"),
            """{"id":"legacy","label":"Old","content":"c","agentName":"a","sessionId":"s","savedAt":"2025-01-01T00:00:00+00:00"}""");

        var store = Store(new SavedResponseOptions());
        var legacy = await store.GetAsync("legacy");

        Assert.IsNotNull(legacy, "An explicit save has no age limit, so a year-old entry survives load");
        Assert.IsNull(legacy.Source);
        Assert.IsFalse(legacy.AutoSaved);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

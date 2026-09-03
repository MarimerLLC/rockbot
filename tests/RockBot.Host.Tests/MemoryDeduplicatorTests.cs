using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers the save path that reinforces an existing entry instead of writing a near-copy of it.
/// </summary>
/// <remarks>
/// The behaviour under test is what makes <see cref="MemoryEntry.ReinforcementCount"/> mean what
/// it says. Before it, the only writers of that field were episode extraction and merges summing
/// their sources, so a live corpus entry reading "reinforced 243×" had in fact been saved fresh
/// 243 times and merged back together — and the corpus grew from 290 to 828 entries in 24 days
/// while consolidation archived 885.
/// </remarks>
[TestClass]
public class MemoryDeduplicatorTests
{
    [TestMethod]
    public async Task StoreWithoutSimilarityLookup_SavesEveryEntry()
    {
        var memory = new PlainMemory();
        var deduplicator = Create(memory);

        var outcome = await deduplicator.SaveOrReinforceAsync(Entry("new", "Rocky prefers concise status"));

        Assert.AreEqual(MemorySaveAction.Created, outcome.Action);
        Assert.AreEqual(1, memory.Entries.Count);
    }

    [TestMethod]
    public async Task Disabled_SavesWithoutConsultingTheLookup()
    {
        var memory = new ScriptedLookupMemory();
        memory.Seed(Entry("existing", "Rocky prefers concise status"));
        memory.NextMatch = Match(memory.Entries[0], 1.0, MemorySimilarityMeasure.Lexical);

        var deduplicator = Create(memory, o => o.DedupeEnabled = false);

        var outcome = await deduplicator.SaveOrReinforceAsync(Entry("new", "Rocky prefers concise status"));

        Assert.AreEqual(MemorySaveAction.Created, outcome.Action);
        Assert.AreEqual(0, memory.LookupCalls);
        Assert.AreEqual(2, memory.Entries.Count);
    }

    [TestMethod]
    [DataRow("feedback/from-agent/status-reports")]
    [DataRow("claim/capability/calendar-mcp/list_events")]
    public async Task ScopedCategories_BypassDeduplication(string category)
    {
        // The contradiction detector supersedes feedback entries and the verifier evicts
        // capability claims; both reason about individual ids, so neither wants its write
        // redirected into an older entry.
        var memory = new ScriptedLookupMemory();
        memory.Seed(Entry("existing", "Always lead with a TL;DR", category: category));
        memory.NextMatch = Match(memory.Entries[0], 1.0, MemorySimilarityMeasure.Lexical);

        var deduplicator = Create(memory);

        var outcome = await deduplicator.SaveOrReinforceAsync(
            Entry("new", "Always lead with a TL;DR", category: category));

        Assert.AreEqual(MemorySaveAction.Created, outcome.Action);
        Assert.AreEqual(0, memory.LookupCalls);
    }

    [TestMethod]
    public async Task SupersededCandidate_IsSavedAsIs()
    {
        var memory = new ScriptedLookupMemory();
        memory.Seed(Entry("existing", "Rocky prefers concise status"));
        memory.NextMatch = Match(memory.Entries[0], 1.0, MemorySimilarityMeasure.Lexical);

        var deduplicator = Create(memory);
        var candidate = Entry("new", "Rocky prefers concise status") with { SupersededBy = "winner" };

        Assert.AreEqual(MemorySaveAction.Created,
            (await deduplicator.SaveOrReinforceAsync(candidate)).Action);
        Assert.AreEqual(0, memory.LookupCalls);
    }

    [TestMethod]
    public async Task EmbeddingScoreBelowTheThreshold_Creates()
    {
        var memory = new ScriptedLookupMemory();
        memory.Seed(Entry("existing", "Rocky prefers concise status"));
        memory.NextMatch = Match(memory.Entries[0], 0.80, MemorySimilarityMeasure.Embedding);

        var deduplicator = Create(memory);

        var outcome = await deduplicator.SaveOrReinforceAsync(Entry("new", "Rocky prefers concise"));

        Assert.AreEqual(MemorySaveAction.Created, outcome.Action);
        Assert.AreEqual(2, memory.Entries.Count);
    }

    [TestMethod]
    public async Task TheTwoMeasuresUseTheirOwnThresholds()
    {
        // 0.70 is a restatement on the Jaccard scale and merely a related subject on the cosine
        // one. Sharing a single number between them would either fold unrelated entries or stop
        // deduplicating BM25-only deployments altogether.
        var lexical = new ScriptedLookupMemory();
        lexical.Seed(Entry("existing", "Rocky prefers concise status"));
        lexical.NextMatch = Match(lexical.Entries[0], 0.70, MemorySimilarityMeasure.Lexical);

        Assert.AreEqual(
            MemorySaveAction.Reinforced,
            (await Create(lexical).SaveOrReinforceAsync(Entry("new", "Rocky prefers concise"))).Action);

        var embedding = new ScriptedLookupMemory();
        embedding.Seed(Entry("existing", "Rocky prefers concise status"));
        embedding.NextMatch = Match(embedding.Entries[0], 0.70, MemorySimilarityMeasure.Embedding);

        Assert.AreEqual(
            MemorySaveAction.Created,
            (await Create(embedding).SaveOrReinforceAsync(Entry("new", "Rocky prefers concise"))).Action);
    }

    [TestMethod]
    public async Task CoveredSpecifics_ReinforceTheExistingEntryInPlace()
    {
        var createdAt = DateTimeOffset.UtcNow.AddDays(-30);
        var existing = new MemoryEntry(
            "existing",
            "The Xebia teams bridge JSON lives on OneDrive Personal, refreshed 2026-08-19.",
            "agent-knowledge/infrastructure",
            ["bridge"],
            createdAt,
            UpdatedAt: createdAt,
            Metadata: new Dictionary<string, string> { ["subjectTime"] = "2026-08-19" },
            ImportanceScore: 0.6f)
        {
            LastSeenAt = createdAt,
            ReinforcementCount = 3,
        };

        // Stamp it as consolidation-reviewed so the test can assert that a pure reinforcement
        // leaves the entry withheld from the next consolidation pass rather than re-opening it.
        existing = existing with
        {
            Metadata = new Dictionary<string, string>(existing.Metadata!)
            {
                [DreamService.ConsolidationReviewedHashKey] = DreamService.ContentFingerprint(existing.Content),
            },
        };

        var memory = new ScriptedLookupMemory();
        memory.Seed(existing);
        memory.NextMatch = Match(existing, 0.95, MemorySimilarityMeasure.Embedding);

        var candidate = new MemoryEntry(
            "new",
            "The Xebia teams bridge JSON is on OneDrive Personal (2026-08-19).",
            "agent-knowledge/infrastructure",
            ["teams"],
            DateTimeOffset.UtcNow,
            Metadata: new Dictionary<string, string> { ["source"] = "mining" },
            ImportanceScore: 0.9f);

        var outcome = await Create(memory).SaveOrReinforceAsync(candidate);

        Assert.AreEqual(MemorySaveAction.Reinforced, outcome.Action);
        Assert.AreEqual("existing", outcome.Id);

        var stored = Assert.ContainsSingle(memory.Entries);
        Assert.AreEqual(existing.Content, stored.Content, "Reinforcement must not rewrite the text.");
        Assert.AreEqual(4, stored.ReinforcementCount);
        Assert.IsGreaterThan(createdAt, stored.LastSeenAt);
        Assert.AreEqual(createdAt, stored.UpdatedAt, "UpdatedAt anchors importance decay; a no-text write must not move it.");
        Assert.AreEqual(0.9f, stored.ImportanceScore);
        CollectionAssert.AreEquivalent(new[] { "bridge", "teams" }, stored.Tags.ToArray());
        Assert.AreEqual("2026-08-19", stored.Metadata!["subjectTime"]);
        Assert.AreEqual("mining", stored.Metadata!["source"]);
        Assert.IsTrue(DreamService.IsReviewedAndUnchanged(stored),
            "An unchanged entry must stay withheld from the next consolidation pass.");
    }

    [TestMethod]
    public async Task ADateSpelledDifferently_CountsAsCovered()
    {
        // The coverage check credits "August 19, 2026" against "2026-08-19", so a restatement that
        // only re-spells the date is evidence rather than a new specific.
        var memory = new ScriptedLookupMemory();
        memory.Seed(Entry("existing", "The bridge refresh runs on 2026-08-19 for the Xebia tenant."));
        memory.NextMatch = Match(memory.Entries[0], 0.95, MemorySimilarityMeasure.Embedding);

        var outcome = await Create(memory).SaveOrReinforceAsync(
            Entry("new", "The Xebia bridge refresh runs on August 19, 2026."));

        Assert.AreEqual(MemorySaveAction.Reinforced, outcome.Action);
    }

    [TestMethod]
    public async Task NewSpecifics_AreAppendedRatherThanDropped()
    {
        var createdAt = DateTimeOffset.UtcNow.AddDays(-10);
        var existing = new MemoryEntry(
            "existing",
            "The Xebia teams bridge JSON lives on OneDrive Personal.",
            null, [], createdAt, UpdatedAt: createdAt)
        {
            Metadata = new Dictionary<string, string>
            {
                [DreamService.ConsolidationReviewedHashKey] =
                    DreamService.ContentFingerprint("The Xebia teams bridge JSON lives on OneDrive Personal."),
            },
        };

        var memory = new ScriptedLookupMemory();
        memory.Seed(existing);
        memory.NextMatch = Match(existing, 0.95, MemorySimilarityMeasure.Embedding);

        var outcome = await Create(memory).SaveOrReinforceAsync(
            Entry("new", "The Xebia teams bridge JSON is served by Marimer LLC."));

        Assert.AreEqual(MemorySaveAction.Extended, outcome.Action);
        Assert.AreEqual("existing", outcome.Id);

        var stored = Assert.ContainsSingle(memory.Entries);
        Assert.AreEqual(
            "The Xebia teams bridge JSON lives on OneDrive Personal.\n\n"
            + "The Xebia teams bridge JSON is served by Marimer LLC.",
            stored.Content);
        Assert.AreEqual(2, stored.ReinforcementCount);
        Assert.IsGreaterThan(createdAt, stored.UpdatedAt!.Value);
        Assert.IsFalse(DreamService.IsReviewedAndUnchanged(stored),
            "Changed text must re-open the entry for the next consolidation pass.");
    }

    [TestMethod]
    public async Task ExtendingPastTheLengthCap_CreatesInstead()
    {
        // Otherwise a subject the agent revisits constantly accretes into one entry that keeps
        // matching itself, and recall surfaces the whole thing for any brush with the topic.
        var existing = Entry("existing", "Blazor Online Class notes. " + new string('x', 400));

        var memory = new ScriptedLookupMemory();
        memory.Seed(existing);
        memory.NextMatch = Match(existing, 0.95, MemorySimilarityMeasure.Embedding);

        var deduplicator = Create(memory, o => o.DedupeMaxExtendedContentLength = 500);

        var outcome = await deduplicator.SaveOrReinforceAsync(
            Entry("new", "Blazor Online Class runs through Marimer LLC. " + new string('y', 200)));

        Assert.AreEqual(MemorySaveAction.Created, outcome.Action);
        Assert.AreEqual(2, memory.Entries.Count);
    }

    [TestMethod]
    public async Task ConcurrentSavesOfTheSameFact_ProduceOneReinforcedEntry()
    {
        // Two background saves of the same fact — two turns mentioning it, or a mining pass
        // overlapping a tool call — would otherwise both look, both find nothing, and both create.
        var memory = new ScriptedLookupMemory();

        // Stand in for a real store: answer from whatever has actually been written so far.
        memory.LookupOverride = candidate =>
        {
            var other = memory.Entries.FirstOrDefault(e => e.Id != candidate.Id);
            return other is null ? null : Match(other, 1.0, MemorySimilarityMeasure.Lexical);
        };

        var deduplicator = Create(memory);

        var content = "The Xebia teams bridge JSON lives on OneDrive Personal.";
        await Task.WhenAll(
            deduplicator.SaveOrReinforceAsync(Entry("a", content)),
            deduplicator.SaveOrReinforceAsync(Entry("b", content)));

        var stored = Assert.ContainsSingle(memory.Entries);
        Assert.AreEqual(2, stored.ReinforcementCount);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MemoryDeduplicator Create(
        ILongTermMemory memory,
        Action<MemoryOptions>? configure = null)
    {
        var options = new MemoryOptions();
        configure?.Invoke(options);

        return new MemoryDeduplicator(
            memory,
            Options.Create(options),
            Options.Create(new AgentProfileOptions()),
            NullLogger<MemoryDeduplicator>.Instance);
    }

    private static MemorySimilarityMatch Match(MemoryEntry entry, double score, MemorySimilarityMeasure measure) =>
        new(entry, score, measure);

    private static MemoryEntry Entry(string id, string content, string? category = null) =>
        new(id, content, category, [], DateTimeOffset.UtcNow);

    /// <summary>A store with no similarity capability at all.</summary>
    private class PlainMemory : ILongTermMemory
    {
        public List<MemoryEntry> Entries { get; } = [];

        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            lock (Entries)
            {
                Entries.RemoveAll(e => e.Id == entry.Id);
                Entries.Add(entry);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(
            MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([.. Entries]);

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Entries.FirstOrDefault(e => e.Id == id));

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            Entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>A store whose similarity answer is scripted by the test.</summary>
    private sealed class ScriptedLookupMemory : PlainMemory, IMemorySimilarityLookup
    {
        public MemorySimilarityMatch? NextMatch { get; set; }
        public Func<MemoryEntry, MemorySimilarityMatch?>? LookupOverride { get; set; }
        public int LookupCalls { get; private set; }

        public void Seed(MemoryEntry entry) => Entries.Add(entry);

        public Task<MemorySimilarityMatch?> FindMostSimilarAsync(
            MemoryEntry candidate, CancellationToken cancellationToken = default)
        {
            LookupCalls++;
            return Task.FromResult(LookupOverride is not null ? LookupOverride(candidate) : NextMatch);
        }
    }
}

/// <summary>Covers the counter behind the "N saved, N reinforced, N extended" pass summaries.</summary>
[TestClass]
public class MemorySaveTallyTests
{
    [TestMethod]
    public void Record_CountsEachActionSeparately()
    {
        var tally = new MemorySaveTally();

        tally.Record(new MemorySaveOutcome(MemorySaveAction.Created, "a"));
        tally.Record(new MemorySaveOutcome(MemorySaveAction.Reinforced, "b"));
        tally.Record(new MemorySaveOutcome(MemorySaveAction.Reinforced, "c"));
        tally.Record(new MemorySaveOutcome(MemorySaveAction.Extended, "d"));

        Assert.AreEqual(1, tally.Saved);
        Assert.AreEqual(2, tally.Reinforced);
        Assert.AreEqual(1, tally.Extended);
    }
}

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Guards the dream's save-time exact-duplicate check: passes that restate a fact already stored
/// reinforce it instead of writing a byte-identical copy for the fold to clean up later.
/// </summary>
[TestClass]
public class DreamSaveTimeExactDedupTests
{
    private const string Fact = "The staging cluster runs a nightly backup at 02:30 and keeps 14 snapshots.";
    private const string Category = "project/infrastructure";

    private string _profileRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _profileRoot = Path.Combine(Path.GetTempPath(), "rockbot-dream-savededup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profileRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_profileRoot))
            Directory.Delete(_profileRoot, recursive: true);
    }

    // ── Shared definition ────────────────────────────────────────────────────

    [TestMethod]
    public void KeyIgnoresWhitespaceAndCategoryCaseButNotContentCase()
    {
        var baseline = DreamService.ExactDuplicateKey(Entry("a", Fact, Category));

        Assert.AreEqual(baseline, DreamService.ExactDuplicateKey(
            Entry("b", "  The staging cluster runs a nightly backup\nat 02:30 and keeps 14 snapshots. ", "Project/Infrastructure ")));
        Assert.AreNotEqual(baseline, DreamService.ExactDuplicateKey(
            Entry("c", Fact.ToUpperInvariant(), Category)));
    }

    [TestMethod]
    public void ObservationTheoriesAreNotEligible()
    {
        Assert.IsFalse(DreamService.IsExactDuplicateEligible(Entry("t", Fact, "observation/theory/sleep")));
        Assert.IsTrue(DreamService.IsExactDuplicateEligible(Entry("p", Fact, Category)));
    }

    // ── Folding a merge into an existing entry ───────────────────────────────

    [TestMethod]
    public void FoldMergeIntoExisting_CombinesCountersAndAppendsProvenance()
    {
        var now = DateTimeOffset.UtcNow;
        var existing = Entry("keep", Fact, Category, daysAgo: 20, tags: ["ops"]) with
        {
            ImportanceScore = 0.5f,
            ReinforcementCount = 4,
            LastSeenAt = now.AddDays(-10),
            UpdatedAt = now.AddDays(-15),
            Metadata = new Dictionary<string, string>
            {
                [DreamService.MergedFromKey] = "older,s1",
                ["subjectTime"] = "2026-01-15",
            },
        };
        var merged = Entry("fresh", Fact, Category, daysAgo: 30, tags: ["Ops", "backup"]) with
        {
            ImportanceScore = 0.8f,
            ReinforcementCount = 3,
            LastSeenAt = now.AddDays(-1),
            UpdatedAt = now,
            Metadata = new Dictionary<string, string>
            {
                [DreamService.MergedFromKey] = "s1,s2",
                [DreamService.MergedAtKey] = "2026-09-01T00:00:00Z",
                [DreamService.ConsolidationReviewedHashKey] = "ABC",
                ["subjectTime"] = "2026-01-16",
                ["source"] = "inferred",
            },
        };
        var sources = new[] { Entry("s1", "x", Category), Entry("s2", "y", Category) };

        var folded = DreamService.FoldMergeIntoExisting(existing, merged, sources, now);

        Assert.AreEqual("keep", folded.Id);
        Assert.AreEqual(Fact, folded.Content);
        Assert.AreEqual(existing.UpdatedAt, folded.UpdatedAt, "No text changed, so the decay anchor holds.");
        Assert.AreEqual(7, folded.ReinforcementCount);
        Assert.AreEqual(0.8f, folded.ImportanceScore);
        Assert.AreEqual(now.AddDays(-1), folded.LastSeenAt);
        Assert.AreEqual(merged.CreatedAt, folded.CreatedAt);
        CollectionAssert.AreEqual(new[] { "ops", "backup" }, folded.Tags.ToArray());

        var metadata = folded.Metadata!;
        Assert.AreEqual("older,s1,s2", metadata[DreamService.MergedFromKey]);
        Assert.AreEqual(now.ToString("O"), metadata[DreamService.MergedAtKey]);
        Assert.AreEqual("2026-01-15", metadata["subjectTime"], "The existing entry's values win.");
        Assert.AreEqual("inferred", metadata["source"]);
        Assert.IsFalse(metadata.ContainsKey(DreamService.ConsolidationReviewedHashKey),
            "A reviewed stamp describes the merge entry's own history, not the existing entry's.");
    }

    // ── Consolidation merges ─────────────────────────────────────────────────

    [TestMethod]
    public async Task AMergeRestatingALiveEntryIsFoldedIntoIt()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("s1", "The staging cluster runs a nightly backup at 02:30.", Category, daysAgo: 5));
        await memory.SaveAsync(Entry("s2", "The staging cluster keeps 14 snapshots.", Category, daysAgo: 3));
        await memory.SaveAsync(Entry("target", Fact, Category, daysAgo: 40) with { ReinforcementCount = 5 });

        var llm = new ScriptedLlmClient($$"""
            {
              "toDelete": ["s1", "s2", "target"],
              "toSave": [{ "content": "{{Fact}}", "category": "{{Category}}", "sourceIds": ["s1", "s2"] }]
            }
            """);
        var service = CreateService(memory, new DreamOptions { Enabled = false, MergeRepairEnabled = false }, llm);

        var (deleted, saved) = await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(1, saved);
        Assert.AreEqual(2, deleted);
        CollectionAssert.AreEquivalent(
            new[] { ("s1", "merged into target"), ("s2", "merged into target") },
            memory.Archived.ToArray(),
            "The prune of the entry that absorbed the merge must be ignored.");

        var live = memory.Snapshot().Where(e => e.ArchivedAt is null).ToList();
        Assert.AreEqual(1, live.Count, "No second copy of the text may be written.");

        var target = live.Single();
        Assert.AreEqual("target", target.Id);
        Assert.AreEqual(7, target.ReinforcementCount);
        Assert.AreEqual("s1,s2", target.Metadata![DreamService.MergedFromKey]);
    }

    [TestMethod]
    public async Task AMergeKeepingOneSourcesTextStillCreatesANewEntry()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("s1", Fact, Category, daysAgo: 5));
        await memory.SaveAsync(Entry("s2", "The staging cluster keeps 14 snapshots.", Category, daysAgo: 3));

        var llm = new ScriptedLlmClient($$"""
            {
              "toDelete": ["s1", "s2"],
              "toSave": [{ "content": "{{Fact}}", "category": "{{Category}}", "sourceIds": ["s1", "s2"] }]
            }
            """);
        var service = CreateService(memory, new DreamOptions { Enabled = false, MergeRepairEnabled = false }, llm);

        var (deleted, saved) = await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(1, saved);
        Assert.AreEqual(2, deleted);

        var created = memory.Snapshot().Single(e => e.ArchivedAt is null);
        Assert.IsFalse(created.Id is "s1" or "s2");
        CollectionAssert.AreEquivalent(
            new[] { ("s1", $"merged into {created.Id}"), ("s2", $"merged into {created.Id}") },
            memory.Archived.ToArray());
    }

    // ── Non-merge passes ─────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("episodic/decision", DisplayName = "episode extraction")]
    [DataRow("user-preferences/inferred", DisplayName = "preference inference")]
    [DataRow("anti-patterns/routing", DisplayName = "tier-routing anti-pattern")]
    [DataRow("anti-patterns/messaging", DisplayName = "DLQ pattern")]
    public async Task AnIdenticalLiveEntryIsReinforcedInsteadOfCopied(string category)
    {
        var memory = new ArchivingStore();
        var existing = Entry("existing", Fact, category, daysAgo: 20) with
        {
            ReinforcementCount = 2,
            ImportanceScore = 0.4f,
        };
        await memory.SaveAsync(existing);

        var service = CreateService(memory, new DreamOptions { Enabled = false });
        var candidate = Entry("candidate", Fact + "\n", category, daysAgo: 0, tags: ["dream"]) with { ImportanceScore = 0.7f };

        var outcome = await service.SaveOrReinforceExactAsync(candidate, CancellationToken.None);

        Assert.AreEqual(MemorySaveAction.Reinforced, outcome.Action);
        Assert.AreEqual("existing", outcome.Id);
        Assert.AreEqual(1, memory.Snapshot().Count, "No new id may be written.");

        var reinforced = memory.Snapshot().Single();
        Assert.AreEqual(3, reinforced.ReinforcementCount);
        Assert.AreEqual(0.7f, reinforced.ImportanceScore);
        Assert.AreEqual(existing.UpdatedAt, reinforced.UpdatedAt);
        CollectionAssert.Contains(reinforced.Tags.ToArray(), "dream");
    }

    [TestMethod]
    public async Task TheOldestOfSeveralMatchesIsReinforced()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("newer", Fact, Category, daysAgo: 2));
        await memory.SaveAsync(Entry("older", Fact, Category, daysAgo: 30));

        var service = CreateService(memory, new DreamOptions { Enabled = false });
        var outcome = await service.SaveOrReinforceExactAsync(Entry("c", Fact, Category, daysAgo: 0), CancellationToken.None);

        Assert.AreEqual("older", outcome.Id);
    }

    [TestMethod]
    public async Task ADifferentCategoryStillCreates()
    {
        var outcome = await SaveAgainst(
            Entry("existing", Fact, "anti-patterns/routing"),
            Entry("candidate", Fact, "anti-patterns/messaging"));

        Assert.AreEqual(MemorySaveAction.Created, outcome.Action);
    }

    [TestMethod]
    public async Task ACaseDifferenceStillCreates()
    {
        var outcome = await SaveAgainst(
            Entry("existing", Fact, Category),
            Entry("candidate", Fact.ToUpperInvariant(), Category));

        Assert.AreEqual(MemorySaveAction.Created, outcome.Action);
    }

    [TestMethod]
    public async Task AnArchivedMatchStillCreates()
    {
        var outcome = await SaveAgainst(
            Entry("existing", Fact, Category) with { ArchivedAt = DateTimeOffset.UtcNow },
            Entry("candidate", Fact, Category));

        Assert.AreEqual(MemorySaveAction.Created, outcome.Action);
    }

    [TestMethod]
    public async Task TheCheckIsOffWhenTheFoldIsOff()
    {
        var outcome = await SaveAgainst(
            Entry("existing", Fact, Category),
            Entry("candidate", Fact, Category),
            new DreamOptions { Enabled = false, MemoryExactDuplicateFoldEnabled = false });

        Assert.AreEqual(MemorySaveAction.Created, outcome.Action);
    }

    [TestMethod]
    public async Task FeedbackEntriesStillCreateEvenWithIdenticalText()
    {
        var outcome = await SaveAgainst(
            Entry("existing", Fact, FeedbackMemoryCategories.UserCorrectionPrefix),
            Entry("candidate", Fact, FeedbackMemoryCategories.UserCorrectionPrefix));

        Assert.AreEqual(MemorySaveAction.Created, outcome.Action);
    }

    [TestMethod]
    public async Task AnUncategorizedCandidateMatchesOnlyUncategorizedEntries()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("filed", Fact, Category, daysAgo: 30));
        await memory.SaveAsync(Entry("loose", Fact, null, daysAgo: 10));

        var service = CreateService(memory, new DreamOptions { Enabled = false });
        var outcome = await service.SaveOrReinforceExactAsync(Entry("c", Fact, null, daysAgo: 0), CancellationToken.None);

        Assert.AreEqual(MemorySaveAction.Reinforced, outcome.Action);
        Assert.AreEqual("loose", outcome.Id);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<MemorySaveOutcome> SaveAgainst(
        MemoryEntry existing, MemoryEntry candidate, DreamOptions? options = null)
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(existing);

        var service = CreateService(memory, options ?? new DreamOptions { Enabled = false });
        var outcome = await service.SaveOrReinforceExactAsync(candidate, CancellationToken.None);

        if (outcome.Action == MemorySaveAction.Created)
            Assert.IsNotNull(await memory.GetAsync(candidate.Id), "A created outcome must have written the candidate.");

        return outcome;
    }

    private static MemoryEntry Entry(
        string id, string content, string? category, int daysAgo = 10, IReadOnlyList<string>? tags = null) =>
        new(id, content, category, tags ?? [], DateTimeOffset.UtcNow.AddDays(-daysAgo));

    private DreamService CreateService(ILongTermMemory memory, DreamOptions options, ILlmClient? llm = null)
    {
        var profile = Options.Create(new AgentProfileOptions { BasePath = _profileRoot });

        return new DreamService(
            memory,
            [],
            llm ?? new ScriptedLlmClient("{}"),
            new AgentWorkSerializer(),
            new StubActivityMonitor(),
            new AgentClock(new ConfigurationBuilder().Build(), profile, NullLogger<AgentClock>.Instance),
            Options.Create(options),
            profile,
            NullLogger<DreamService>.Instance);
    }

    private sealed class ScriptedLlmClient(params string[] responses) : ILlmClient
    {
        public List<string> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        {
            Calls.Add(string.Join("\n", messages.Select(m => m.Text)));
            var index = Math.Min(Calls.Count - 1, responses.Length - 1);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responses[index])));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options,
            CancellationToken cancellationToken) =>
            GetResponseAsync(messages, options, cancellationToken);
    }

    private sealed class StubActivityMonitor : IUserActivityMonitor
    {
        public void RecordActivity() { }
        public bool IsUserActive(TimeSpan idleThreshold) => false;
    }

    /// <summary>
    /// In-memory store whose search honours the category prefix and live-only defaults of the file
    /// store, so the lookup is exercised against the same filter it relies on in production.
    /// </summary>
    private sealed class ArchivingStore : ILongTermMemory
    {
        private readonly Dictionary<string, MemoryEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Id, string Reason)> Archived { get; } = [];

        public IReadOnlyList<MemoryEntry> Snapshot() => [.. _entries.Values];

        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            _entries[entry.Id] = entry;
            return Task.CompletedTask;
        }

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.GetValueOrDefault(id));

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(
            MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>(
            [
                .. _entries.Values
                    .Where(e => e.ArchivedAt is null)
                    .Where(e => criteria.Category is null
                        || (e.Category is not null
                            && (e.Category.Equals(criteria.Category, StringComparison.OrdinalIgnoreCase)
                                || e.Category.StartsWith(criteria.Category + "/", StringComparison.OrdinalIgnoreCase))))
                    .Take(criteria.MaxResults)
            ]);

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            _entries.Remove(id);
            return Task.CompletedTask;
        }

        public Task ArchiveAsync(string id, string reason, CancellationToken cancellationToken = default)
        {
            Archived.Add((id, reason));
            if (_entries.TryGetValue(id, out var entry))
                _entries[id] = entry with { ArchivedAt = DateTimeOffset.UtcNow, ArchiveReason = reason };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}

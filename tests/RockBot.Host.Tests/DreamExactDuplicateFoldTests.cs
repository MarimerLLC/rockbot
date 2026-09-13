using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// Guards the deterministic exact-duplicate fold at the top of the consolidation pass.
/// </summary>
/// <remarks>
/// Identical copies used to depend on the dream model choosing to merge them. When it declined
/// once, every copy carried a matching reviewed stamp, the cluster read as settled, and the copies
/// stayed live indefinitely. Identical text needs no judgement, so the fold makes none.
/// </remarks>
[TestClass]
public class DreamExactDuplicateFoldTests
{
    private const string Fact = "Rocky has a Red Fletcher show at White Rock Lounge on 2026-10-26.";

    private string _profileRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _profileRoot = Path.Combine(Path.GetTempPath(), "rockbot-dream-fold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profileRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_profileRoot))
            Directory.Delete(_profileRoot, recursive: true);
    }

    // ── Grouping ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void IdenticalContentInTheSameCategoryGroups_OldestFirst()
    {
        var groups = DreamService.FindExactDuplicateGroups(
        [
            Entry("new", Fact, "personal/events", daysAgo: 1),
            Entry("old", Fact, "personal/events", daysAgo: 20),
            Entry("mid", Fact, "personal/events", daysAgo: 5),
        ]);

        Assert.AreEqual(1, groups.Count);
        CollectionAssert.AreEqual(new[] { "old", "mid", "new" }, groups[0].Select(e => e.Id).ToArray());
    }

    [TestMethod]
    public void WhitespaceDifferencesStillGroup()
    {
        var groups = DreamService.FindExactDuplicateGroups(
        [
            Entry("a", Fact, "personal/events"),
            Entry("b", "  Rocky has a Red Fletcher show\nat White Rock Lounge on 2026-10-26.\r\n", "personal/events"),
        ]);

        Assert.AreEqual(1, groups.Count);
    }

    [TestMethod]
    public void CaseOrWordingDifferencesDoNotGroup()
    {
        // Anything looser than whitespace is a judgement about meaning, and that stays with
        // consolidation.
        var groups = DreamService.FindExactDuplicateGroups(
        [
            Entry("a", Fact, "personal/events"),
            Entry("b", Fact.ToUpperInvariant(), "personal/events"),
            Entry("c", "A verified calendar event shows a Red Fletcher show at White Rock Lounge on 2026-10-26.", "personal/events"),
        ]);

        Assert.AreEqual(0, groups.Count);
    }

    [TestMethod]
    public void IdenticalContentInDifferentCategoriesDoesNotGroup()
    {
        var groups = DreamService.FindExactDuplicateGroups(
        [
            Entry("a", Fact, "user-preferences/work"),
            Entry("b", Fact, "work/business"),
        ]);

        Assert.AreEqual(0, groups.Count);
    }

    [TestMethod]
    public void CategoryComparisonIgnoresCase()
    {
        var groups = DreamService.FindExactDuplicateGroups(
        [
            Entry("a", Fact, "Personal/Events"),
            Entry("b", Fact, "personal/events"),
        ]);

        Assert.AreEqual(1, groups.Count);
    }

    [TestMethod]
    public void ArchivedSupersededFeedbackAndCapabilityClaimEntriesAreSkipped()
    {
        var groups = DreamService.FindExactDuplicateGroups(
        [
            Entry("live", Fact, "personal/events"),
            Entry("archived", Fact, "personal/events") with { ArchivedAt = DateTimeOffset.UtcNow },
            Entry("superseded", Fact, "personal/events") with { SupersededBy = "live" },
            Entry("fb1", Fact, FeedbackMemoryCategories.UserCorrectionPrefix),
            Entry("fb2", Fact, FeedbackMemoryCategories.UserCorrectionPrefix),
            Entry("cap1", Fact, CapabilityClaimCategories.For("todo", "add_task")),
            Entry("cap2", Fact, CapabilityClaimCategories.For("todo", "add_task")),
        ]);

        Assert.AreEqual(0, groups.Count,
            "Only one live, unscoped copy remains, so nothing forms a group.");
    }

    // ── Folding ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void FoldCombinesEvidenceAndKeepsTheSurvivorsText()
    {
        var now = DateTimeOffset.UtcNow;
        var survivor = Entry("old", Fact, "personal/events", daysAgo: 20, tags: ["music", "calendar"]) with
        {
            ImportanceScore = 0.6f,
            ReinforcementCount = 6,
            LastSeenAt = now.AddDays(-10),
            UpdatedAt = now.AddDays(-15),
        };
        var copy = Entry("new", Fact + "\n", "personal/events", daysAgo: 2, tags: ["Calendar", "live-show"]) with
        {
            ImportanceScore = 0.88f,
            ReinforcementCount = 1,
            LastSeenAt = now.AddDays(-2),
        };

        var folded = DreamService.FoldExactDuplicates(survivor, [copy], now);

        Assert.AreEqual("old", folded.Id);
        Assert.AreEqual(Fact, folded.Content);
        CollectionAssert.AreEqual(new[] { "music", "calendar", "live-show" }, folded.Tags.ToArray());
        Assert.AreEqual(0.88f, folded.ImportanceScore);
        Assert.AreEqual(7, folded.ReinforcementCount);
        Assert.AreEqual(now.AddDays(-2), folded.LastSeenAt);
        Assert.AreEqual(survivor.CreatedAt, folded.CreatedAt);
        Assert.AreEqual(survivor.UpdatedAt, folded.UpdatedAt, "UpdatedAt anchors decay; no text changed.");
        Assert.AreEqual("new", folded.Metadata![DreamService.FoldedFromKey]);
        Assert.IsTrue(folded.Metadata.ContainsKey(DreamService.FoldedAtKey));
    }

    [TestMethod]
    public void FoldCopiesMissingMetadataButNotPerCopyBookkeeping()
    {
        var survivor = Entry("old", Fact, "personal/events", daysAgo: 20) with
        {
            Metadata = new Dictionary<string, string>
            {
                ["subjectTime"] = "2026-10-26",
                [DreamService.FoldedFromKey] = "earlier",
            },
        };
        var copy = Entry("new", Fact, "personal/events") with
        {
            Metadata = new Dictionary<string, string>
            {
                ["subjectTime"] = "2026-10-27",
                ["source"] = "inferred",
                [DreamService.MergedFromKey] = "x,y",
                [DreamService.MergedAtKey] = "2026-09-01T00:00:00Z",
                [DreamService.ConsolidationReviewedHashKey] = "ABC",
                [DreamService.ConsolidationReviewedAtKey] = "2026-09-01T00:00:00Z",
                [DreamService.ConsolidationRejectedClusterKey] = "cluster",
                [DreamService.ConsolidationRejectedAtKey] = "2026-09-01T00:00:00Z",
            },
        };

        var metadata = DreamService.FoldExactDuplicates(survivor, [copy], DateTimeOffset.UtcNow).Metadata!;

        Assert.AreEqual("2026-10-26", metadata["subjectTime"], "The survivor's own values win.");
        Assert.AreEqual("inferred", metadata["source"]);
        Assert.AreEqual("earlier,new", metadata[DreamService.FoldedFromKey]);
        Assert.IsFalse(metadata.ContainsKey(DreamService.MergedFromKey),
            "mergedFrom means model prose built from sources; a fold wrote no text.");
        Assert.IsFalse(metadata.ContainsKey(DreamService.MergedAtKey));
        Assert.IsFalse(metadata.ContainsKey(DreamService.ConsolidationReviewedHashKey));
        Assert.IsFalse(metadata.ContainsKey(DreamService.ConsolidationReviewedAtKey));
        Assert.IsFalse(metadata.ContainsKey(DreamService.ConsolidationRejectedClusterKey));
        Assert.IsFalse(metadata.ContainsKey(DreamService.ConsolidationRejectedAtKey));
    }

    // ── The pass ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ThePassFoldsDuplicatesWithoutAnLlmCallAndArchivesCopiesMergedIntoTheSurvivor()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("old", Fact, "personal/events", daysAgo: 20));
        await memory.SaveAsync(Entry("mid", Fact, "personal/events", daysAgo: 10));
        await memory.SaveAsync(Entry("new", Fact, "personal/events", daysAgo: 1));

        var llm = new ScriptedLlmClient("""{ "toDelete": [], "toSave": [] }""");
        var service = CreateService(memory, new DreamOptions { Enabled = false }, llm);

        var (deleted, _) = await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(2, deleted);
        CollectionAssert.AreEquivalent(
            new[] { ("mid", "merged into old"), ("new", "merged into old") },
            memory.Archived.ToArray());

        var survivor = memory.Snapshot().Single(e => e.Id == "old");
        Assert.IsNull(survivor.ArchivedAt);
        Assert.AreEqual(3, survivor.ReinforcementCount);
        Assert.AreEqual(0, llm.Calls.Count,
            "One live entry is left, so there is nothing for the model to consolidate.");
    }

    [TestMethod]
    public async Task TheFoldRunsEvenWhenLlmConsolidationIsDisabled()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("old", Fact, "personal/events", daysAgo: 20));
        await memory.SaveAsync(Entry("new", Fact, "personal/events", daysAgo: 1));

        var llm = new ScriptedLlmClient("""{ "toDelete": [], "toSave": [] }""");
        var service = CreateService(
            memory, new DreamOptions { Enabled = false, MemoryConsolidationEnabled = false }, llm);

        var (deleted, _) = await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(1, deleted);
        Assert.AreEqual(0, llm.Calls.Count);
    }

    [TestMethod]
    public async Task TheFoldCanBeTurnedOff()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("old", Fact, "personal/events", daysAgo: 20));
        await memory.SaveAsync(Entry("new", Fact, "personal/events", daysAgo: 1));

        var service = CreateService(
            memory,
            new DreamOptions { Enabled = false, MemoryConsolidationEnabled = false, MemoryExactDuplicateFoldEnabled = false },
            new ScriptedLlmClient("{}"));

        await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(0, memory.Archived.Count);
    }

    [TestMethod]
    public async Task ThePauseMarkerStopsTheFold()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("old", Fact, "personal/events", daysAgo: 20));
        await memory.SaveAsync(Entry("new", Fact, "personal/events", daysAgo: 1));

        var auditDir = Path.Combine(_profileRoot, MemoryAuditFiles.DefaultBasePath);
        Directory.CreateDirectory(auditDir);
        await File.WriteAllTextAsync(
            Path.Combine(auditDir, MemoryAuditFiles.ConsolidationPausedFile),
            """{"reason":"test","snapshotId":"s","pausedAt":"2026-09-04T04:00:00+00:00"}""");

        var service = CreateService(memory, new DreamOptions { Enabled = false }, new ScriptedLlmClient("{}"));

        await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(0, memory.Archived.Count, "A fold archives, so the circuit breaker applies to it.");
    }

    [TestMethod]
    public async Task TheLlmPassSeesTheFoldedCorpus()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("old", Fact, "personal/events", daysAgo: 20));
        await memory.SaveAsync(Entry("new", Fact, "personal/events", daysAgo: 1));
        await memory.SaveAsync(Entry("other", "The user prefers Central time for scheduling.", "user-preferences", daysAgo: 3));

        var llm = new ScriptedLlmClient("""{ "toDelete": [], "toSave": [] }""");
        var service = CreateService(memory, new DreamOptions { Enabled = false }, llm);

        await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(1, llm.Calls.Count);
        StringAssert.Contains(llm.Calls[0], "[ID:old]");
        StringAssert.Contains(llm.Calls[0], "[ID:other]");
        Assert.IsFalse(llm.Calls[0].Contains("[ID:new]"), "An archived copy must not be offered to the model.");
    }

    [TestMethod]
    public void TheFoldIsOnByDefault()
    {
        Assert.IsTrue(new DreamOptions().MemoryExactDuplicateFoldEnabled);
    }

    [TestMethod]
    public void TheFoldToggleBindsFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dream:MemoryExactDuplicateFoldEnabled"] = "false",
            })
            .Build();
        var opts = new DreamOptions();
        config.GetSection("Dream").Bind(opts);

        Assert.IsFalse(opts.MemoryExactDuplicateFoldEnabled);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MemoryEntry Entry(
        string id, string content, string? category, int daysAgo = 10, IReadOnlyList<string>? tags = null) =>
        new(id, content, category, tags ?? [], DateTimeOffset.UtcNow.AddDays(-daysAgo));

    private DreamService CreateService(ILongTermMemory memory, DreamOptions options, ILlmClient llm)
    {
        var profile = Options.Create(new AgentProfileOptions { BasePath = _profileRoot });

        return new DreamService(
            memory,
            [],
            llm,
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
                [.. _entries.Values.Where(e => e.ArchivedAt is null)]);

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

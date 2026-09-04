using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// The two hooks the memory audit needs from the dream cycle: a rejection stamp it can read
/// back, and a pause marker it can write.
/// </summary>
/// <remarks>
/// Both are deliberately small. A rejection previously existed only as a log line, so "the same
/// cluster is refused every cycle forever" was invisible past Loki's retention; and a circuit
/// breaker that the auditor could not trip would leave the destructive pass running through
/// exactly the incident it detected.
/// </remarks>
[TestClass]
public class DreamConsolidationAuditHooksTests
{
    private const string SourceA = "Rockford Duane Lhotka uses timezone America/Chicago.";
    private const string SourceB = "Accounts span Microsoft and Marimer LLC.";
    private const string LossyMerge = "The user has accounts across providers and a default timezone.";

    private string _profileRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _profileRoot = Path.Combine(Path.GetTempPath(), "rockbot-dream-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profileRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_profileRoot))
            Directory.Delete(_profileRoot, recursive: true);
    }

    [TestMethod]
    public async Task ARejectedMergeStampsItsSourcesWithTheClusterAndTime()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("a", SourceA));
        await memory.SaveAsync(Entry("b", SourceB));

        // Repair disabled so the rejection is terminal and the stamp is the only outcome.
        var service = CreateService(
            memory, new DreamOptions { Enabled = false, MergeRepairEnabled = false },
            new ScriptedLlmClient(ConsolidationResponse(LossyMerge)));

        await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        var a = memory.Snapshot().Single(e => e.Id == "a");
        var b = memory.Snapshot().Single(e => e.Id == "b");

        Assert.IsNotNull(a.Metadata);
        Assert.IsTrue(a.Metadata.ContainsKey(DreamService.ConsolidationRejectedClusterKey));
        Assert.IsTrue(a.Metadata.ContainsKey(DreamService.ConsolidationRejectedAtKey));

        Assert.AreEqual(
            a.Metadata[DreamService.ConsolidationRejectedClusterKey],
            b.Metadata![DreamService.ConsolidationRejectedClusterKey],
            "Both sources belong to the same rejected cluster.");

        Assert.IsTrue(DateTimeOffset.TryParse(
            a.Metadata[DreamService.ConsolidationRejectedAtKey], out _));

        Assert.AreEqual(0, memory.Archived.Count, "A rejection still leaves the sources alone.");
    }

    [TestMethod]
    public void TheClusterHashIsOrderIndependent()
    {
        Assert.AreEqual(
            DreamService.RejectedClusterHash(["b", "a", "c"]),
            DreamService.RejectedClusterHash(["a", "c", "b"]));

        Assert.AreNotEqual(
            DreamService.RejectedClusterHash(["a", "b"]),
            DreamService.RejectedClusterHash(["a", "b", "c"]));
    }

    [TestMethod]
    public async Task ConsolidationSkipsEntirelyWhileThePauseMarkerExists()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("a", SourceA));
        await memory.SaveAsync(Entry("b", SourceB));

        var auditDir = Path.Combine(_profileRoot, MemoryAuditFiles.DefaultBasePath);
        Directory.CreateDirectory(auditDir);
        await File.WriteAllTextAsync(
            Path.Combine(auditDir, MemoryAuditFiles.ConsolidationPausedFile),
            """{"reason":"test","snapshotId":"s","pausedAt":"2026-09-04T04:00:00+00:00"}""");

        var llm = new ScriptedLlmClient(ConsolidationResponse("anything"));
        var service = CreateService(memory, new DreamOptions { Enabled = false }, llm);

        var (deleted, saved) = await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.AreEqual(0, deleted);
        Assert.AreEqual(0, saved);
        Assert.AreEqual(0, llm.Calls.Count, "A paused pass must not even spend an LLM call.");
    }

    [TestMethod]
    public async Task ConsolidationRunsNormallyWhenNoMarkerExists()
    {
        var memory = new ArchivingStore();
        await memory.SaveAsync(Entry("a", SourceA));
        await memory.SaveAsync(Entry("b", SourceB));

        var llm = new ScriptedLlmClient(ConsolidationResponse(LossyMerge));
        var service = CreateService(memory, new DreamOptions { Enabled = false }, llm);

        await service.RunMemoryConsolidationPassAsync(CancellationToken.None);

        Assert.IsTrue(llm.Calls.Count > 0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ConsolidationResponse(string mergedContent) =>
        $$"""
        {
          "toDelete": ["a", "b"],
          "toSave": [{ "content": "{{mergedContent}}", "sourceIds": ["a", "b"] }]
        }
        """;

    private static MemoryEntry Entry(string id, string content) =>
        new(id, content, null, [], DateTimeOffset.UtcNow.AddDays(-10));

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

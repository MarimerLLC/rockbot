using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Host.Tests;

/// <summary>
/// End-to-end behaviour of the audit against a real temp store.
/// </summary>
/// <remarks>
/// Every test constructs the service with a memory store whose write paths throw. That is the
/// central claim of the whole feature — the audit measures the corpus and cannot damage it —
/// and it is worth enforcing mechanically rather than by review.
/// </remarks>
[TestClass]
public class MemoryAuditServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private string _profileRoot = null!;
    private string _memoryRoot = null!;
    private string _auditRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _profileRoot = Path.Combine(Path.GetTempPath(), "rockbot-audit-svc-" + Guid.NewGuid().ToString("N"));
        _memoryRoot = Path.Combine(_profileRoot, "memory");
        _auditRoot = Path.Combine(_profileRoot, "memory-audit");
        Directory.CreateDirectory(_memoryRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_profileRoot))
            Directory.Delete(_profileRoot, recursive: true);
    }

    [TestMethod]
    public async Task RunAudit_WritesTheTrendRowStateAndReport_WithoutTouchingTheStore()
    {
        WriteEntry(new MemoryEntry("a", "the agent stores memories on the shared volume", null, [], Now(-30)));
        WriteEntry(new MemoryEntry("b", "the deploy pipeline pushes tagged images", null, [], Now(-20)));

        var service = CreateService();

        var snapshot = await service.RunAuditAsync(CancellationToken.None);

        Assert.IsNotNull(snapshot, "The work slot was free, so the run should have completed.");
        Assert.AreEqual(2, snapshot.Live);
        Assert.IsNull(snapshot.PreviousTakenAt);

        Assert.IsTrue(File.Exists(Path.Combine(_auditRoot, MemoryAuditFiles.SnapshotsFile)));
        Assert.IsTrue(File.Exists(Path.Combine(_auditRoot, MemoryAuditFiles.StateFile)));
        Assert.IsTrue(File.Exists(Path.Combine(_auditRoot, MemoryAuditFiles.LatestReport)));

        StringAssert.Contains(
            await File.ReadAllTextAsync(Path.Combine(_auditRoot, MemoryAuditFiles.LatestReport)),
            "# Memory audit");
    }

    [TestMethod]
    public async Task ASecondRunComputesDeltasAgainstTheFirst()
    {
        WriteEntry(Entry("a"));
        WriteEntry(Entry("b"));

        var service = CreateService();
        await service.RunAuditAsync(CancellationToken.None);

        // One new fact, one destroyed behind the store's back — the shape the audit exists for.
        WriteEntry(Entry("c"));
        File.Delete(Path.Combine(_memoryRoot, "b.json"));

        var second = await service.RunAuditAsync(CancellationToken.None);

        Assert.IsNotNull(second);
        Assert.IsNotNull(second.PreviousTakenAt);
        Assert.AreEqual(1, second.CreatedSinceLast);
        Assert.AreEqual(1, second.HardDeletedSinceLast);
        Assert.AreEqual(1, second.HardDeletedOutsidePurge);
        Assert.AreEqual(MemoryAuditStatuses.Alert, second.Status);

        var lines = await File.ReadAllLinesAsync(Path.Combine(_auditRoot, MemoryAuditFiles.SnapshotsFile));
        Assert.AreEqual(2, lines.Count(l => !string.IsNullOrWhiteSpace(l)),
            "The trend file is appended to, never rewritten.");
    }

    [TestMethod]
    public async Task ABusyAgentDefersTheRunRatherThanCompetingWithIt()
    {
        WriteEntry(Entry("a"));

        var service = CreateService(serializer: new BusySerializer());

        Assert.IsNull(await service.RunAuditAsync(CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(_auditRoot, MemoryAuditFiles.SnapshotsFile)));
    }

    [TestMethod]
    public async Task AnAlertIsPublishedToTheScheduledSystemSession()
    {
        WriteEntry(Entry("a"));
        WriteEntry(Entry("b"));

        var publisher = new RecordingPublisher();
        var service = CreateService(publisher: publisher);

        await service.RunAuditAsync(CancellationToken.None);
        Assert.AreEqual(0, publisher.Published.Count, "A healthy first run says nothing.");

        File.Delete(Path.Combine(_memoryRoot, "b.json"));
        await service.RunAuditAsync(CancellationToken.None);

        Assert.AreEqual(1, publisher.Published.Count);
        var (topic, reply) = publisher.Published[0];
        StringAssert.Contains(topic, UserProxyTopics.UserResponse);
        Assert.AreEqual(WellKnownSessions.ScheduledSystem, reply.SessionId);
        Assert.AreEqual("memory-audit", reply.Origin?.Channel);
        StringAssert.Contains(reply.Content, "no-hard-delete-outside-purge");
    }

    [TestMethod]
    public async Task NoAlertIsPublishedWhenAlertingIsTurnedOff()
    {
        WriteEntry(Entry("a"));
        WriteEntry(Entry("b"));

        var publisher = new RecordingPublisher();
        var service = CreateService(
            publisher: publisher,
            options: new MemoryAuditOptions { AlertOnAttention = false, CopyReportToShared = false });

        await service.RunAuditAsync(CancellationToken.None);
        File.Delete(Path.Combine(_memoryRoot, "b.json"));
        await service.RunAuditAsync(CancellationToken.None);

        Assert.AreEqual(0, publisher.Published.Count);
    }

    [TestMethod]
    public async Task TheConsolidationPauseMarkerIsWrittenOnlyWhenOptedIn()
    {
        WriteEntry(Entry("a"));
        WriteEntry(Entry("b"));

        var optedOut = CreateService();
        await optedOut.RunAuditAsync(CancellationToken.None);
        File.Delete(Path.Combine(_memoryRoot, "b.json"));
        await optedOut.RunAuditAsync(CancellationToken.None);

        var markerPath = Path.Combine(_auditRoot, MemoryAuditFiles.ConsolidationPausedFile);
        Assert.IsFalse(File.Exists(markerPath), "The circuit breaker is opt-in.");

        WriteEntry(Entry("d"));
        var optedIn = CreateService(options: new MemoryAuditOptions
        {
            PauseConsolidationOnAlert = true,
            CopyReportToShared = false
        });
        await optedIn.RunAuditAsync(CancellationToken.None);
        File.Delete(Path.Combine(_memoryRoot, "d.json"));
        await optedIn.RunAuditAsync(CancellationToken.None);

        Assert.IsTrue(File.Exists(markerPath));
        using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(markerPath));
        StringAssert.Contains(
            marker.RootElement.GetProperty("reason").GetString()!, "hard-deleted outside the retention purge");
    }

    [TestMethod]
    public async Task Prune_KeepsSnapshotsInsideTheRetentionWindowAndDropsOldReports()
    {
        Directory.CreateDirectory(_auditRoot);

        var snapshotsPath = Path.Combine(_auditRoot, MemoryAuditFiles.SnapshotsFile);
        await File.WriteAllLinesAsync(snapshotsPath,
        [
            Line(DateTimeOffset.UtcNow.AddDays(-500)),
            Line(DateTimeOffset.UtcNow.AddDays(-10)),
            Line(DateTimeOffset.UtcNow)
        ]);

        var staleReport = Path.Combine(_auditRoot, "report-2020-01-01.md");
        await File.WriteAllTextAsync(staleReport, "old");
        File.SetLastWriteTimeUtc(staleReport, DateTime.UtcNow.AddDays(-400));

        var freshReport = Path.Combine(_auditRoot, "report-2026-09-04.md");
        await File.WriteAllTextAsync(freshReport, "new");

        var service = CreateService();
        var removed = await service.PruneAsync(new LogRetentionPolicy(TimeSpan.FromDays(30), 1000, 10_000));

        Assert.AreEqual(2, removed, "One stale report plus one out-of-retention snapshot row.");
        Assert.IsFalse(File.Exists(staleReport));
        Assert.IsTrue(File.Exists(freshReport));

        var kept = (await File.ReadAllLinesAsync(snapshotsPath)).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.AreEqual(2, kept.Count, "The 400-day snapshot retention outlives the 30-day file policy.");
    }

    [TestMethod]
    public async Task TheReportIsCopiedToTheSharedVolumeWhenEnabled()
    {
        WriteEntry(Entry("a"));

        var shared = Path.Combine(_profileRoot, "shared-exports");
        var service = CreateService(options: new MemoryAuditOptions
        {
            CopyReportToShared = true,
            SharedReportDirectory = shared
        });

        await service.RunAuditAsync(CancellationToken.None);

        Assert.AreEqual(1, Directory.GetFiles(shared, "memory-audit-*.md").Length);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Line(DateTimeOffset takenAt) =>
        JsonSerializer.Serialize(
            new MemoryAuditSnapshot { SnapshotId = "s", TakenAt = takenAt }, JsonOptions);

    private static DateTimeOffset Now(int daysAgo) => DateTimeOffset.UtcNow.AddDays(daysAgo);

    private static MemoryEntry Entry(string id) =>
        new(id, $"a durable fact about subject {id} worth keeping in long term memory", null, [], Now(-30));

    private void WriteEntry(MemoryEntry entry) =>
        File.WriteAllText(
            Path.Combine(_memoryRoot, $"{entry.Id}.json"),
            JsonSerializer.Serialize(entry, JsonOptions));

    private MemoryAuditService CreateService(
        MemoryAuditOptions? options = null,
        IAgentWorkSerializer? serializer = null,
        IMessagePublisher? publisher = null) =>
        new(new ThrowingWriteMemory(),
            serializer ?? new AgentWorkSerializer(),
            new AgentClock(
                new ConfigurationBuilder().Build(),
                Options.Create(new AgentProfileOptions { BasePath = _profileRoot }),
                NullLogger<AgentClock>.Instance),
            Options.Create(options ?? new MemoryAuditOptions { CopyReportToShared = false }),
            Options.Create(new DreamOptions()),
            Options.Create(new MemoryOptions { BasePath = "memory" }),
            Options.Create(new AgentProfileOptions { BasePath = _profileRoot }),
            NullLogger<MemoryAuditService>.Instance,
            llmClient: null,
            publisher: publisher,
            agent: publisher is null ? null : new AgentIdentity("TestBot", "inst"));

    /// <summary>
    /// A store whose every write path throws. The audit holds <see cref="ILongTermMemory"/> only
    /// for the optional duplicate probe; if it ever reaches for a writer, these tests fail loudly.
    /// </summary>
    private sealed class ThrowingWriteMemory : ILongTermMemory
    {
        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The memory audit must never write to the store.");

        public Task<ContentEditResult> EditAsync(
            string id, string oldText, string newText, bool replaceAll = false,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The memory audit must never edit the store.");

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The memory audit must never delete from the store.");

        public Task ArchiveAsync(string id, string reason, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The memory audit must never archive.");

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(
            MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(null);

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class BusySerializer : IAgentWorkSerializer
    {
        public Task<IAsyncDisposable> AcquireForUserAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IScheduledTaskSlot?> TryAcquireForScheduledAsync(CancellationToken ct) =>
            Task.FromResult<IScheduledTaskSlot?>(null);
    }

    private sealed class RecordingPublisher : IMessagePublisher
    {
        public List<(string Topic, AgentReply Reply)> Published { get; } = [];

        public Task PublishAsync(
            string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Published.Add((topic, envelope.GetPayload<AgentReply>()!));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

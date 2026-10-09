using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Memory;

namespace RockBot.Host.Tests;

/// <summary>
/// Issue #668: working-memory reads carry when an entry was stored and who wrote it, and
/// patrol snapshots are bannered as possibly stale so a cached copy cannot pass for live
/// state. Covers the store (stored-at/writer recorded, persisted, and preserved across a
/// restart), the tool rendering, and backward compatibility with files written before the
/// writer was recorded.
/// </summary>
[TestClass]
public class WorkingMemoryProvenanceTests
{
    private const string PatrolNamespace = "patrol/heartbeat-patrol";
    private const string SnapshotKey = "shared/patrol/todos-latest";

    private string _dir = null!;
    private MemoryCache _cache = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rockbot-wm-provenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _cache.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Store: stored-at and writer ───────────────────────────────────────

    [TestMethod]
    public async Task SaveThroughTools_RecordsStoredAtAndWriter()
    {
        var store = NewFileStore();
        var tools = new WorkingMemoryTools(store, PatrolNamespace, NullLogger.Instance);
        var before = DateTimeOffset.UtcNow;

        await tools.SaveToWorkingMemory(SnapshotKey, "- buy milk", ttl_minutes: 300);

        var entry = await store.GetEntryAsync(SnapshotKey);
        Assert.IsNotNull(entry);
        Assert.AreEqual(PatrolNamespace, entry.Writer);
        Assert.IsTrue(entry.HasStoredAt);
        Assert.IsTrue(entry.StoredAt >= before.AddSeconds(-1) && entry.StoredAt <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [TestMethod]
    public async Task SaveThroughTools_PersistsStoredAtAndWriterToDisk()
    {
        var store = NewFileStore();
        var tools = new WorkingMemoryTools(store, PatrolNamespace, NullLogger.Instance);

        await tools.SaveToWorkingMemory(SnapshotKey, "- buy milk", ttl_minutes: 300);

        var json = await File.ReadAllTextAsync(Path.Combine(_dir, "shared.json"));
        using var doc = JsonDocument.Parse(json);
        var persisted = doc.RootElement[0];
        Assert.AreEqual(PatrolNamespace, persisted.GetProperty("writer").GetString());
        Assert.IsTrue(persisted.GetProperty("storedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [TestMethod]
    public async Task Restart_PreservesOriginalStoredAtAndWriter()
    {
        // Before #668 a restore re-set every entry, stamping it with the restart time — a
        // two-day-old patrol snapshot looked like it had just been written.
        var storedAt = DateTimeOffset.UtcNow.AddDays(-2);
        await WriteGroupFileAsync("shared", $$"""
            [{"key":"{{SnapshotKey}}","value":"- buy milk","storedAt":"{{storedAt:O}}",
              "expiresAt":"{{DateTimeOffset.UtcNow.AddHours(3):O}}","category":"patrol","tags":[],
              "writer":"{{PatrolNamespace}}"}]
            """);

        var store = NewFileStore();
        await store.StartAsync(CancellationToken.None);

        var entry = await store.GetEntryAsync(SnapshotKey);
        Assert.IsNotNull(entry);
        Assert.AreEqual(storedAt.ToUnixTimeSeconds(), entry.StoredAt.ToUnixTimeSeconds());
        Assert.AreEqual(PatrolNamespace, entry.Writer);
        await store.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Edit_KeepsWriter()
    {
        var store = NewFileStore();
        var tools = new WorkingMemoryTools(store, PatrolNamespace, NullLogger.Instance);
        await tools.SaveToWorkingMemory(SnapshotKey, "- buy milk", ttl_minutes: 300);

        await store.EditAsync(SnapshotKey, "milk", "bread");

        var entry = await store.GetEntryAsync(SnapshotKey);
        Assert.AreEqual("- buy bread", entry!.Value);
        Assert.AreEqual(PatrolNamespace, entry.Writer);
    }

    // ── get_from_working_memory rendering ─────────────────────────────────

    [TestMethod]
    public async Task Get_PatrolSnapshot_HasBannerWithWriterTimestampAndAge()
    {
        var store = NewFileStore();
        await new WorkingMemoryTools(store, PatrolNamespace, NullLogger.Instance)
            .SaveToWorkingMemory(SnapshotKey, "- buy milk", ttl_minutes: 600);
        var entry = await store.GetEntryAsync(SnapshotKey);

        // Read two hours later, from a user session.
        var reader = UserTools(store, entry!.StoredAt.AddHours(2));
        var result = await reader.GetFromWorkingMemory(SnapshotKey);

        var header = result.Split('\n')[0];
        StringAssert.StartsWith(header, $"[snapshot written by {PatrolNamespace} at {WorkingMemoryProvenance.FormatUtc(entry.StoredAt)} (2 hours ago)");
        StringAssert.Contains(header, "may be stale");
        StringAssert.Contains(header, "call the live tool and prefer it if they disagree");
        Assert.IsFalse(header.Contains("STALE"), "Inside the stale_after window the banner is not escalated");
        Assert.IsTrue(result.EndsWith("\n- buy milk"), "The snapshot data is still returned");
    }

    [TestMethod]
    public async Task Get_PatrolSnapshot_PastStaleAfter_SaysStale()
    {
        var store = NewFileStore();
        await new WorkingMemoryTools(store, PatrolNamespace, NullLogger.Instance)
            .SaveToWorkingMemory(SnapshotKey, "- buy milk", ttl_minutes: 600);
        var entry = await store.GetEntryAsync(SnapshotKey);

        // Default stale_after is 6h; read 7 hours later.
        var result = await UserTools(store, entry!.StoredAt.AddHours(7)).GetFromWorkingMemory(SnapshotKey);

        StringAssert.StartsWith(result, "[STALE SNAPSHOT — written by " + PatrolNamespace);
        StringAssert.Contains(result, "(7 hours ago)");
        StringAssert.Contains(result, "6h stale_after window");
        StringAssert.Contains(result, "call the live tool");
        Assert.IsTrue(result.EndsWith("\n- buy milk"), "Stale data is flagged, not hidden");
    }

    [TestMethod]
    public async Task Get_PatrolSnapshot_HonoursConfiguredStaleAfter()
    {
        var store = NewFileStore();
        await new WorkingMemoryTools(store, PatrolNamespace, NullLogger.Instance)
            .SaveToWorkingMemory(SnapshotKey, "- buy milk", ttl_minutes: 600);
        var entry = await store.GetEntryAsync(SnapshotKey);
        var options = new WorkingMemoryOptions { SnapshotStaleAfter = TimeSpan.FromMinutes(30) };

        var result = await UserTools(store, entry!.StoredAt.AddMinutes(45), options).GetFromWorkingMemory(SnapshotKey);

        StringAssert.StartsWith(result, "[STALE SNAPSHOT");
        StringAssert.Contains(result, "30m stale_after window");
    }

    [TestMethod]
    public async Task Get_NonPatrolKey_HasProvenanceLineButNoSnapshotBanner()
    {
        var store = NewFileStore();
        await new WorkingMemoryTools(store, "session/abc", NullLogger.Instance)
            .SaveToWorkingMemory("draft", "Dear Tina,", ttl_minutes: 60);
        var entry = await store.GetEntryAsync("session/abc/draft");

        var result = await UserTools(store, entry!.StoredAt.AddMinutes(5)).GetFromWorkingMemory("session/abc/draft");

        Assert.AreEqual(
            $"[stored {WorkingMemoryProvenance.FormatUtc(entry.StoredAt)} (5 minutes ago) by session/abc]\nDear Tina,",
            result);
        Assert.IsFalse(result.Contains("snapshot", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Get_ConfiguredSnapshotPrefix_ReplacesDefault()
    {
        var store = NewFileStore();
        var writer = new WorkingMemoryTools(store, PatrolNamespace, NullLogger.Instance);
        await writer.SaveToWorkingMemory("shared/inbox/digest", "3 unread", ttl_minutes: 60);
        await writer.SaveToWorkingMemory(SnapshotKey, "- buy milk", ttl_minutes: 60);
        var options = new WorkingMemoryOptions { SnapshotKeyPrefixes = ["shared/inbox/"] };
        var reader = UserTools(store, DateTimeOffset.UtcNow, options);

        StringAssert.StartsWith(await reader.GetFromWorkingMemory("shared/inbox/digest"), "[snapshot written by");
        StringAssert.StartsWith(await reader.GetFromWorkingMemory(SnapshotKey), "[stored ");
    }

    // ── Backward compatibility ────────────────────────────────────────────

    [TestMethod]
    public async Task LegacyEntry_WithoutStoredAtOrWriter_RendersStoredAtUnknown()
    {
        // A file written before stored-at/writer were recorded: neither property present.
        await WriteGroupFileAsync("session", $$"""
            [{"key":"session/old/notes","value":"legacy payload",
              "expiresAt":"{{DateTimeOffset.UtcNow.AddHours(1):O}}","category":null,"tags":null}]
            """);
        var store = NewFileStore();
        await store.StartAsync(CancellationToken.None);

        var entry = await store.GetEntryAsync("session/old/notes");
        Assert.IsNotNull(entry, "Legacy entry must deserialize and restore");
        Assert.IsFalse(entry.HasStoredAt);
        Assert.IsNull(entry.Writer);

        var result = await UserTools(store, DateTimeOffset.UtcNow).GetFromWorkingMemory("session/old/notes");
        Assert.AreEqual("[stored at unknown]\nlegacy payload", result);
        await store.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task LegacyEntry_WithStoredAtButNoWriter_RendersTimestampWithoutWriter()
    {
        var storedAt = DateTimeOffset.UtcNow.AddHours(-3);
        await WriteGroupFileAsync("session", $$"""
            [{"key":"session/old/notes","value":"legacy payload","storedAt":"{{storedAt:O}}",
              "expiresAt":"{{DateTimeOffset.UtcNow.AddHours(1):O}}","category":null,"tags":null}]
            """);
        var store = NewFileStore();
        await store.StartAsync(CancellationToken.None);

        var result = await UserTools(store, storedAt.AddHours(3)).GetFromWorkingMemory("session/old/notes");

        Assert.AreEqual($"[stored {WorkingMemoryProvenance.FormatUtc(storedAt)} (3 hours ago)]\nlegacy payload", result);
        await store.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task LegacyPatrolSnapshot_WithoutStoredAt_IsTreatedAsStale()
    {
        await WriteGroupFileAsync("shared", $$"""
            [{"key":"{{SnapshotKey}}","value":"- buy milk",
              "expiresAt":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}]
            """);
        var store = NewFileStore();
        await store.StartAsync(CancellationToken.None);

        var result = await UserTools(store, DateTimeOffset.UtcNow).GetFromWorkingMemory(SnapshotKey);

        StringAssert.StartsWith(result, "[STALE SNAPSHOT — written by an unknown writer at an unknown time");
        await store.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task LegacyEntry_WithoutStoredAt_CanStillBeEdited()
    {
        await WriteGroupFileAsync("session", $$"""
            [{"key":"session/old/notes","value":"legacy payload",
              "expiresAt":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}]
            """);
        var store = NewFileStore();
        await store.StartAsync(CancellationToken.None);

        var result = await store.EditAsync("session/old/notes", "legacy", "edited");

        Assert.IsTrue(result.IsSuccess, result.Error);
        var entry = await store.GetEntryAsync("session/old/notes");
        Assert.AreEqual("edited payload", entry!.Value);
        Assert.IsTrue(entry.ExpiresAt <= DateTimeOffset.UtcNow.AddHours(1).AddMinutes(1),
            "With no stored-at the TTL window must not be computed from year 0001");
        await store.StopAsync(CancellationToken.None);
    }

    // ── search_working_memory rendering ───────────────────────────────────

    [TestMethod]
    public async Task Search_ListsStoredAtWriterAndSnapshotMarker()
    {
        var store = NewFileStore();
        var patrol = new WorkingMemoryTools(store, PatrolNamespace, NullLogger.Instance);
        await patrol.SaveToWorkingMemory(SnapshotKey, "- buy milk", ttl_minutes: 600);
        await patrol.SaveToWorkingMemory("shared/drafts/reply", "Dear Tina,", ttl_minutes: 600);
        var entry = await store.GetEntryAsync(SnapshotKey);

        var result = await UserTools(store, entry!.StoredAt.AddDays(2)).SearchWorkingMemory(@namespace: "shared");

        var snapshotLine = result.Split('\n').Single(l => l.Contains(SnapshotKey));
        StringAssert.Contains(snapshotLine, $"stored {WorkingMemoryProvenance.FormatUtc(entry.StoredAt)} (2 days ago) by {PatrolNamespace}");
        StringAssert.Contains(snapshotLine, "[STALE SNAPSHOT");

        var draftLine = result.Split('\n').Single(l => l.Contains("shared/drafts/reply"));
        StringAssert.Contains(draftLine, $"by {PatrolNamespace}");
        Assert.IsFalse(draftLine.Contains("SNAPSHOT", StringComparison.OrdinalIgnoreCase));
    }

    // ── Humanized age ─────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(0, "just now")]
    [DataRow(1, "1 minute ago")]
    [DataRow(59, "59 minutes ago")]
    [DataRow(60, "1 hour ago")]
    [DataRow(5 * 60, "5 hours ago")]
    [DataRow(24 * 60, "1 day ago")]
    [DataRow(3 * 24 * 60, "3 days ago")]
    [DataRow(-5, "just now")]
    public void HumanizeAge_Formats(int minutes, string expected) =>
        Assert.AreEqual(expected, WorkingMemoryProvenance.HumanizeAge(TimeSpan.FromMinutes(minutes)));

    // ── Helpers ───────────────────────────────────────────────────────────

    private FileWorkingMemory NewFileStore()
    {
        var options = Options.Create(new WorkingMemoryOptions { BasePath = _dir });
        var inner = new HybridCacheWorkingMemory(
            _cache, options, Options.Create(new EmbeddingOptions()),
            EmbeddingTextPreparer.ForTests(), NullLogger<HybridCacheWorkingMemory>.Instance);
        return new FileWorkingMemory(inner, options,
            Options.Create(new AgentProfileOptions { BasePath = _dir }),
            NullLogger<FileWorkingMemory>.Instance);
    }

    private static WorkingMemoryTools UserTools(IWorkingMemory store, DateTimeOffset now,
        WorkingMemoryOptions? options = null) =>
        new(store, "session/user1", NullLogger.Instance, options: options, timeProvider: new FixedTime(now));

    private Task WriteGroupFileAsync(string group, string json) =>
        File.WriteAllTextAsync(Path.Combine(_dir, $"{group}.json"), json);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

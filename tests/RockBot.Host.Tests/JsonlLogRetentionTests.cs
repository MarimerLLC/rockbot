using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class JsonlLogRetentionTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-retention-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── TrimToLastLinesAsync ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Trim_KeepsLastLines_AndReturnsRemovedCount()
    {
        var path = Path.Combine(_tempDir, "global.jsonl");
        var lines = Enumerable.Range(0, 100).Select(i => $"{{\"n\":{i}}}").ToArray();
        await File.WriteAllLinesAsync(path, lines);

        var removed = await JsonlLogRetention.TrimToLastLinesAsync(path, maxLines: 10, NullLogger.Instance);

        Assert.AreEqual(90, removed);
        var kept = await File.ReadAllLinesAsync(path);
        Assert.AreEqual(10, kept.Length);
        Assert.AreEqual("{\"n\":90}", kept[0]);
        Assert.AreEqual("{\"n\":99}", kept[^1]);
    }

    [TestMethod]
    public async Task Trim_UnderBudget_IsNoOp()
    {
        var path = Path.Combine(_tempDir, "global.jsonl");
        var lines = Enumerable.Range(0, 5).Select(i => $"line-{i}").ToArray();
        await File.WriteAllLinesAsync(path, lines);

        var removed = await JsonlLogRetention.TrimToLastLinesAsync(path, maxLines: 10, NullLogger.Instance);

        Assert.AreEqual(0, removed);
        Assert.AreEqual(5, (await File.ReadAllLinesAsync(path)).Length);
    }

    [TestMethod]
    public async Task Trim_NonPositiveMaxLines_IsNoOp()
    {
        var path = Path.Combine(_tempDir, "global.jsonl");
        await File.WriteAllLinesAsync(path, new[] { "a", "b", "c" });

        var removed = await JsonlLogRetention.TrimToLastLinesAsync(path, maxLines: 0, NullLogger.Instance);

        Assert.AreEqual(0, removed);
        Assert.AreEqual(3, (await File.ReadAllLinesAsync(path)).Length);
    }

    [TestMethod]
    public async Task Trim_MissingFile_ReturnsZero()
    {
        var path = Path.Combine(_tempDir, "does-not-exist.jsonl");
        var removed = await JsonlLogRetention.TrimToLastLinesAsync(path, maxLines: 10, NullLogger.Instance);
        Assert.AreEqual(0, removed);
    }

    [TestMethod]
    public async Task Trim_LeavesNoTempFileBehind()
    {
        var path = Path.Combine(_tempDir, "global.jsonl");
        await File.WriteAllLinesAsync(path, Enumerable.Range(0, 50).Select(i => i.ToString()).ToArray());

        await JsonlLogRetention.TrimToLastLinesAsync(path, maxLines: 5, NullLogger.Instance);

        Assert.IsFalse(File.Exists(path + ".tmp"));
    }

    // ── PruneAgedFilesAsync ───────────────────────────────────────────────────

    [TestMethod]
    public async Task Prune_DeletesFilesOlderThanMaxAge()
    {
        var dir = Path.Combine(_tempDir, "sessions");
        Directory.CreateDirectory(dir);

        var oldFile = WriteSessionFile(dir, "old", ageDays: 40);
        var freshFile = WriteSessionFile(dir, "fresh", ageDays: 1);

        var deleted = await JsonlLogRetention.PruneAgedFilesAsync(
            dir, maxAge: TimeSpan.FromDays(30), maxFiles: 0, "*.jsonl", NullLogger.Instance);

        Assert.AreEqual(1, deleted);
        Assert.IsFalse(File.Exists(oldFile));
        Assert.IsTrue(File.Exists(freshFile));
    }

    [TestMethod]
    public async Task Prune_CapsFileCount_KeepingNewest()
    {
        var dir = Path.Combine(_tempDir, "sessions");
        Directory.CreateDirectory(dir);

        // Five files, all within the age window, distinct ages so ordering is deterministic.
        var paths = new List<(string path, int age)>();
        for (var i = 0; i < 5; i++)
            paths.Add((WriteSessionFile(dir, $"s{i}", ageDays: i), i));

        var deleted = await JsonlLogRetention.PruneAgedFilesAsync(
            dir, maxAge: TimeSpan.Zero, maxFiles: 2, "*.jsonl", NullLogger.Instance);

        Assert.AreEqual(3, deleted);
        // The two most recently written (smallest age) survive.
        Assert.IsTrue(File.Exists(paths[0].path));
        Assert.IsTrue(File.Exists(paths[1].path));
        Assert.IsFalse(File.Exists(paths[4].path));
    }

    [TestMethod]
    public async Task Prune_AgeThenCount_Combined()
    {
        var dir = Path.Combine(_tempDir, "sessions");
        Directory.CreateDirectory(dir);

        WriteSessionFile(dir, "ancient", ageDays: 100);
        var keep1 = WriteSessionFile(dir, "k1", ageDays: 1);
        var keep2 = WriteSessionFile(dir, "k2", ageDays: 2);
        var dropByCount = WriteSessionFile(dir, "k3", ageDays: 3);

        // Age prunes "ancient"; then count cap of 2 drops the oldest survivor.
        var deleted = await JsonlLogRetention.PruneAgedFilesAsync(
            dir, maxAge: TimeSpan.FromDays(30), maxFiles: 2, "*.jsonl", NullLogger.Instance);

        Assert.AreEqual(2, deleted);
        Assert.IsTrue(File.Exists(keep1));
        Assert.IsTrue(File.Exists(keep2));
        Assert.IsFalse(File.Exists(dropByCount));
    }

    [TestMethod]
    public async Task Prune_DisabledDimensions_NoOp()
    {
        var dir = Path.Combine(_tempDir, "sessions");
        Directory.CreateDirectory(dir);
        WriteSessionFile(dir, "old", ageDays: 100);
        WriteSessionFile(dir, "new", ageDays: 1);

        var deleted = await JsonlLogRetention.PruneAgedFilesAsync(
            dir, maxAge: TimeSpan.Zero, maxFiles: 0, "*.jsonl", NullLogger.Instance);

        Assert.AreEqual(0, deleted);
        Assert.AreEqual(2, Directory.GetFiles(dir, "*.jsonl").Length);
    }

    [TestMethod]
    public async Task Prune_MissingDirectory_ReturnsZero()
    {
        var deleted = await JsonlLogRetention.PruneAgedFilesAsync(
            Path.Combine(_tempDir, "nope"), TimeSpan.FromDays(1), 1, "*.jsonl", NullLogger.Instance);
        Assert.AreEqual(0, deleted);
    }

    // ── Store integration ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task PerSessionStore_PruneAsync_DropsAgedSessionFiles()
    {
        var skillOptions = Options.Create(new SkillOptions { UsageBasePath = Path.Combine(_tempDir, "skill-usage") });
        var profileOptions = Options.Create(new AgentProfileOptions { BasePath = _tempDir });
        var store = new FileSkillUsageStore(skillOptions, profileOptions, NullLogger<FileSkillUsageStore>.Instance);

        await store.AppendAsync(new SkillInvocationEvent(
            Id: "a", SkillName: "x", SessionId: "stale", Timestamp: DateTimeOffset.UtcNow));
        await store.AppendAsync(new SkillInvocationEvent(
            Id: "b", SkillName: "y", SessionId: "fresh", Timestamp: DateTimeOffset.UtcNow));

        var dir = Path.Combine(_tempDir, "skill-usage");
        File.SetLastWriteTimeUtc(Path.Combine(dir, "stale.jsonl"), DateTime.UtcNow.AddDays(-40));

        var removed = await store.PruneAsync(new LogRetentionPolicy(
            MaxFileAge: TimeSpan.FromDays(30), MaxFilesPerDirectory: 0, MaxLinesPerFile: 0));

        Assert.AreEqual(1, removed);
        Assert.AreEqual(0, (await store.GetBySessionAsync("stale")).Count);
        Assert.AreEqual(1, (await store.GetBySessionAsync("fresh")).Count);
    }

    [TestMethod]
    public async Task SingleFileStore_PruneAsync_TrimsToLineBudget()
    {
        var profileOptions = Options.Create(new AgentProfileOptions { BasePath = _tempDir });
        var store = new FileSkillResourceUsageStore(profileOptions, NullLogger<FileSkillResourceUsageStore>.Instance);

        for (var i = 0; i < 25; i++)
            await store.RecordCheckoutAsync("skill", $"file-{i}.txt", "session", DateTimeOffset.UtcNow);

        var removed = await store.PruneAsync(new LogRetentionPolicy(
            MaxFileAge: TimeSpan.Zero, MaxFilesPerDirectory: 0, MaxLinesPerFile: 10));

        Assert.AreEqual(15, removed);
        var path = Path.Combine(_tempDir, "skill-resource-usage.jsonl");
        Assert.AreEqual(10, (await File.ReadAllLinesAsync(path)).Length);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string WriteSessionFile(string dir, string name, int ageDays)
    {
        var path = Path.Combine(dir, $"{name}.jsonl");
        File.WriteAllText(path, "{\"x\":1}" + Environment.NewLine);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }
}

using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

[TestClass]
public sealed class FileScheduledTaskStoreTests
{
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rockbot-sched-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private FileScheduledTaskStore CreateStore()
    {
        var filePath = Path.Combine(_tempDir, "scheduled-tasks.json");
        return new FileScheduledTaskStore(filePath, NullLogger<FileScheduledTaskStore>.Instance);
    }

    private static ScheduledTask MakeTask(string name, string cron = "0 8 * * *", string description = "Do something") =>
        new(name, cron, description, DateTimeOffset.UtcNow);

    // ── SaveAsync / GetAsync ──────────────────────────────────────────────────

    [TestMethod]
    public async Task SaveAsync_NewTask_CanBeRetrieved()
    {
        var store = CreateStore();
        var task = MakeTask("check-email");

        await store.SaveAsync(task);
        var retrieved = await store.GetAsync("check-email");

        Assert.IsNotNull(retrieved);
        Assert.AreEqual("check-email", retrieved.Name);
        Assert.AreEqual("0 8 * * *", retrieved.CronExpression);
        Assert.AreEqual("Do something", retrieved.Description);
    }

    [TestMethod]
    public async Task SaveAsync_ExistingTask_Replaces()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeTask("my-task", cron: "0 8 * * *"));

        var updated = MakeTask("my-task", cron: "0 9 * * *", description: "Updated");
        await store.SaveAsync(updated);

        var retrieved = await store.GetAsync("my-task");
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("0 9 * * *", retrieved.CronExpression);
        Assert.AreEqual("Updated", retrieved.Description);
    }

    [TestMethod]
    public async Task GetAsync_UnknownName_ReturnsNull()
    {
        var store = CreateStore();
        var result = await store.GetAsync("nonexistent");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task SaveAsync_PreservesClientCapabilities()
    {
        var store = CreateStore();
        var task = new ScheduledTask(
            Name: "rich-summary",
            CronExpression: "0 8 * * *",
            Description: "Daily summary with charts",
            CreatedAt: DateTimeOffset.UtcNow,
            ClientCapabilities: RockBot.UserProxy.ClientCapabilityPresets.Blazor);

        await store.SaveAsync(task);
        var retrieved = await store.GetAsync("rich-summary");

        Assert.IsNotNull(retrieved);
        Assert.AreEqual(RockBot.UserProxy.ClientCapabilityPresets.Blazor, retrieved.ClientCapabilities);
    }

    [TestMethod]
    public async Task GetAsync_LegacyFileWithoutClientCapabilities_DefaultsToNone()
    {
        // Files persisted before the ClientCapabilities field was added should round-trip
        // with the default value, not blow up deserialization. The on-disk format is a
        // JSON array of tasks (see FileScheduledTaskStore.WriteAllAsync).
        var filePath = Path.Combine(_tempDir, "scheduled-tasks.json");
        await File.WriteAllTextAsync(filePath,
            """
            [
              {
                "name": "legacy-task",
                "cronExpression": "0 8 * * *",
                "description": "Pre-capability task",
                "createdAt": "2026-01-01T00:00:00+00:00",
                "lastFiredAt": null,
                "runOnce": false,
                "isSystemTask": false,
                "directive": null
              }
            ]
            """);
        var store = new FileScheduledTaskStore(filePath, NullLogger<FileScheduledTaskStore>.Instance);

        var retrieved = await store.GetAsync("legacy-task");

        Assert.IsNotNull(retrieved);
        Assert.AreEqual(RockBot.UserProxy.ClientCapabilities.None, retrieved.ClientCapabilities);
    }

    // ── ListAsync ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ListAsync_NoTasks_ReturnsEmptyList()
    {
        var store = CreateStore();
        var list = await store.ListAsync();
        Assert.AreEqual(0, list.Count);
    }

    [TestMethod]
    public async Task ListAsync_MultipleTasks_ReturnsSortedByName()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeTask("z-task"));
        await store.SaveAsync(MakeTask("a-task"));
        await store.SaveAsync(MakeTask("m-task"));

        var list = await store.ListAsync();

        Assert.AreEqual(3, list.Count);
        Assert.AreEqual("a-task", list[0].Name);
        Assert.AreEqual("m-task", list[1].Name);
        Assert.AreEqual("z-task", list[2].Name);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task DeleteAsync_ExistingTask_ReturnsTrue()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeTask("to-delete"));

        var result = await store.DeleteAsync("to-delete");

        Assert.IsTrue(result);
        Assert.IsNull(await store.GetAsync("to-delete"));
    }

    [TestMethod]
    public async Task DeleteAsync_UnknownTask_ReturnsFalse()
    {
        var store = CreateStore();
        var result = await store.DeleteAsync("nonexistent");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task DeleteAsync_LeavesOtherTasksIntact()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeTask("keep-me"));
        await store.SaveAsync(MakeTask("delete-me"));

        await store.DeleteAsync("delete-me");

        var list = await store.ListAsync();
        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("keep-me", list[0].Name);
    }

    // ── UpdateLastFiredAsync ──────────────────────────────────────────────────

    [TestMethod]
    public async Task UpdateLastFiredAsync_SetsTimestamp()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeTask("fire-me"));

        var firedAt = new DateTimeOffset(2026, 2, 19, 8, 0, 0, TimeSpan.Zero);
        await store.UpdateLastFiredAsync("fire-me", firedAt);

        var retrieved = await store.GetAsync("fire-me");
        Assert.IsNotNull(retrieved);
        Assert.AreEqual(firedAt, retrieved.LastFiredAt);
    }

    [TestMethod]
    public async Task UpdateLastFiredAsync_UnknownTask_IsNoOp()
    {
        var store = CreateStore();
        // Should not throw
        await store.UpdateLastFiredAsync("nonexistent", DateTimeOffset.UtcNow);
    }

    // ── Directive ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SaveAsync_WithDirective_RoundTripsAcrossRestarts()
    {
        var filePath = Path.Combine(_tempDir, "scheduled-tasks.json");
        var store1 = new FileScheduledTaskStore(filePath, NullLogger<FileScheduledTaskStore>.Instance);

        var task = MakeTask("with-directive") with { Directive = "## Checklist\n- one\n- two" };
        await store1.SaveAsync(task);

        var store2 = new FileScheduledTaskStore(filePath, NullLogger<FileScheduledTaskStore>.Instance);
        var retrieved = await store2.GetAsync("with-directive");

        Assert.IsNotNull(retrieved);
        Assert.AreEqual("## Checklist\n- one\n- two", retrieved.Directive);
    }

    [TestMethod]
    public async Task UpdateDirectiveAsync_SetsDirectiveAndPreservesOtherFields()
    {
        var store = CreateStore();
        var firedAt = new DateTimeOffset(2026, 2, 19, 8, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(MakeTask("patrol", cron: "0 8 * * *", description: "Patrol"));
        await store.UpdateLastFiredAsync("patrol", firedAt);

        await store.UpdateDirectiveAsync("patrol", "new directive body");

        var retrieved = await store.GetAsync("patrol");
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("new directive body", retrieved.Directive);
        Assert.AreEqual("0 8 * * *", retrieved.CronExpression, "Cron must be preserved.");
        Assert.AreEqual("Patrol", retrieved.Description, "Description must be preserved.");
        Assert.AreEqual(firedAt, retrieved.LastFiredAt, "LastFiredAt must be preserved.");
    }

    [TestMethod]
    public async Task UpdateDirectiveAsync_UnknownTask_IsNoOp()
    {
        var store = CreateStore();
        // Should not throw
        await store.UpdateDirectiveAsync("nonexistent", "body");
        Assert.IsNull(await store.GetAsync("nonexistent"));
    }

    // ── EditDirectiveAsync ────────────────────────────────────────────────────

    [TestMethod]
    public async Task EditDirectiveAsync_ChangesOnlyTheMatchedText()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeTask("patrol"));
        await store.UpdateDirectiveAsync("patrol", "## Checklist\n- check plans\n- check todos");

        var result = await store.EditDirectiveAsync("patrol", "- check todos", "- check todos\n- check email");

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(1, result.ReplacementCount);
        var retrieved = await store.GetAsync("patrol");
        Assert.AreEqual("## Checklist\n- check plans\n- check todos\n- check email", retrieved!.Directive);
    }

    [TestMethod]
    public async Task EditDirectiveAsync_PreservesOtherFields()
    {
        var store = CreateStore();
        var firedAt = new DateTimeOffset(2026, 2, 19, 8, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(MakeTask("patrol", cron: "0 8 * * *", description: "Patrol"));
        await store.UpdateLastFiredAsync("patrol", firedAt);
        await store.UpdateDirectiveAsync("patrol", "watch the queue");

        await store.EditDirectiveAsync("patrol", "queue", "backlog");

        var retrieved = await store.GetAsync("patrol");
        Assert.AreEqual("watch the backlog", retrieved!.Directive);
        Assert.AreEqual("0 8 * * *", retrieved.CronExpression);
        Assert.AreEqual("Patrol", retrieved.Description);
        Assert.AreEqual(firedAt, retrieved.LastFiredAt);
    }

    [TestMethod]
    public async Task EditDirectiveAsync_UnknownTask_Refuses()
    {
        var store = CreateStore();

        var result = await store.EditDirectiveAsync("nonexistent", "a", "b");

        Assert.IsFalse(result.IsSuccess, "Unlike UpdateDirectiveAsync, an edit must report a missing task.");
        StringAssert.Contains(result.Error, "nonexistent");
    }

    [TestMethod]
    public async Task EditDirectiveAsync_TaskWithNoDirective_Refuses()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeTask("patrol"));

        var result = await store.EditDirectiveAsync("patrol", "a", "b");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "no directive yet");
    }

    [TestMethod]
    public async Task EditDirectiveAsync_AmbiguousMatch_Refuses_AndWritesNothing()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeTask("patrol"));
        await store.UpdateDirectiveAsync("patrol", "check\ncheck");

        var result = await store.EditDirectiveAsync("patrol", "check", "verify");

        Assert.IsFalse(result.IsSuccess);
        StringAssert.Contains(result.Error, "occurs 2 times");
        Assert.AreEqual("check\ncheck", (await store.GetAsync("patrol"))!.Directive);
    }

    [TestMethod]
    public async Task EditDirectiveAsync_ReplaceAll_ReplacesEveryOccurrence()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeTask("patrol"));
        await store.UpdateDirectiveAsync("patrol", "check\ncheck");

        var result = await store.EditDirectiveAsync("patrol", "check", "verify", replaceAll: true);

        Assert.IsTrue(result.IsSuccess, result.Error);
        Assert.AreEqual(2, result.ReplacementCount);
        Assert.AreEqual("verify\nverify", (await store.GetAsync("patrol"))!.Directive);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Tasks_PersistAcrossStoreInstances()
    {
        var filePath = Path.Combine(_tempDir, "scheduled-tasks.json");

        // Write with first instance
        var store1 = new FileScheduledTaskStore(filePath, NullLogger<FileScheduledTaskStore>.Instance);
        await store1.SaveAsync(MakeTask("persisted-task"));

        // Read with second instance (simulates restart)
        var store2 = new FileScheduledTaskStore(filePath, NullLogger<FileScheduledTaskStore>.Instance);
        var retrieved = await store2.GetAsync("persisted-task");

        Assert.IsNotNull(retrieved);
        Assert.AreEqual("persisted-task", retrieved.Name);
    }

    // ── Case insensitivity ────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetAsync_IsCaseInsensitive()
    {
        var store = CreateStore();
        await store.SaveAsync(MakeTask("My-Task"));

        var retrieved = await store.GetAsync("my-task");
        Assert.IsNotNull(retrieved);
    }
}

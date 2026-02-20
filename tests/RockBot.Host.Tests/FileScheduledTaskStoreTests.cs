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

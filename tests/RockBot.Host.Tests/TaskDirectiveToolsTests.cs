using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

/// <summary>
/// Verifies <see cref="TaskDirectiveTools.UpdateTaskDirective"/> writes through to the store
/// for the task it was constructed against, and surfaces the new body on subsequent reads —
/// the round-trip the next fire of the scheduled task depends on.
/// </summary>
[TestClass]
public sealed class TaskDirectiveToolsTests
{
    private string _tempDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rockbot-task-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task UpdateTaskDirective_WritesContentVisibleOnNextRead()
    {
        var store = CreateStore();
        await store.SaveAsync(new ScheduledTask(
            "patrol", "0 8 * * *", "do stuff", DateTimeOffset.UtcNow, IsSystemTask: true));

        var tools = new TaskDirectiveTools(store, "patrol", NullLogger<TaskDirectiveTools>.Instance);

        var result = await tools.UpdateTaskDirective("- check email\n- check calendar");

        var retrieved = await store.GetAsync("patrol");
        Assert.IsNotNull(retrieved);
        Assert.AreEqual("- check email\n- check calendar", retrieved.Directive);
        StringAssert.Contains(result, "patrol");
    }

    [TestMethod]
    public async Task UpdateTaskDirective_OnlyAffectsTheBoundTask()
    {
        var store = CreateStore();
        await store.SaveAsync(new ScheduledTask(
            "patrol", "0 8 * * *", "do stuff", DateTimeOffset.UtcNow, IsSystemTask: true));
        await store.SaveAsync(new ScheduledTask(
            "weekly-report", "0 9 * * 1", "report", DateTimeOffset.UtcNow));

        var tools = new TaskDirectiveTools(store, "patrol", NullLogger<TaskDirectiveTools>.Instance);
        await tools.UpdateTaskDirective("patrol body");

        Assert.AreEqual("patrol body", (await store.GetAsync("patrol"))!.Directive);
        Assert.IsNull((await store.GetAsync("weekly-report"))!.Directive,
            "Updating one task's directive must not bleed into a sibling task.");
    }

    [TestMethod]
    public async Task UpdateTaskDirective_UnknownTask_DoesNotThrow()
    {
        var store = CreateStore();
        var tools = new TaskDirectiveTools(store, "missing", NullLogger<TaskDirectiveTools>.Instance);

        var result = await tools.UpdateTaskDirective("ignored");

        // Underlying UpdateDirectiveAsync is a no-op; the tool returns its acknowledgement
        // string but no task was written.
        Assert.IsNull(await store.GetAsync("missing"));
        StringAssert.Contains(result, "missing");
    }

    private FileScheduledTaskStore CreateStore() =>
        new(Path.Combine(_tempDir, "scheduled-tasks.json"),
            NullLogger<FileScheduledTaskStore>.Instance);
}

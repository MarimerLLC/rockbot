using System.Security.Claims;
using A2A;
using Microsoft.AspNetCore.Http;

using A2ATaskStatus = A2A.TaskStatus;

namespace RockBot.A2A.Gateway.Tests;

[TestClass]
public class FileTaskStoreTests
{
    private static IHttpContextAccessor CreateAccessor(string callerId = "test-caller")
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, callerId)
        ], "test"));
        return new HttpContextAccessor { HttpContext = context };
    }

    private static AgentTask MakeTask(string id, TaskState state = TaskState.Completed) => new()
    {
        Id = id,
        ContextId = $"ctx-{id}",
        Status = new A2ATaskStatus
        {
            State = state,
            Timestamp = DateTimeOffset.UtcNow
        }
    };

    [TestMethod]
    public async Task SaveAndRetrieve_RoundTrips()
    {
        var store = new FileTaskStore(CreateAccessor(), filePath: null);
        var task = MakeTask("t1");

        await store.SaveTaskAsync("t1", task, CancellationToken.None);
        var retrieved = await store.GetTaskAsync("t1", CancellationToken.None);

        Assert.IsNotNull(retrieved);
        Assert.AreEqual("t1", retrieved!.Id);
        Assert.AreEqual(TaskState.Completed, retrieved.Status?.State);
    }

    [TestMethod]
    public async Task GetTask_NotFound_ReturnsNull()
    {
        var store = new FileTaskStore(CreateAccessor(), filePath: null);

        var result = await store.GetTaskAsync("nonexistent", CancellationToken.None);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DeleteTask_RemovesEntry()
    {
        var store = new FileTaskStore(CreateAccessor(), filePath: null);
        await store.SaveTaskAsync("t1", MakeTask("t1"), CancellationToken.None);

        await store.DeleteTaskAsync("t1", CancellationToken.None);

        var result = await store.GetTaskAsync("t1", CancellationToken.None);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ListTasks_ReturnsAllForCaller()
    {
        var store = new FileTaskStore(CreateAccessor("alice"), filePath: null);
        await store.SaveTaskAsync("t1", MakeTask("t1"), CancellationToken.None);
        await store.SaveTaskAsync("t2", MakeTask("t2"), CancellationToken.None);

        var result = await store.ListTasksAsync(
            new ListTasksRequest { Tenant = "alice" }, CancellationToken.None);

        Assert.AreEqual(2, result.Tasks.Count);
        Assert.AreEqual(2, result.TotalSize);
    }

    [TestMethod]
    public async Task ListTasks_CallerScoping_FiltersOtherCallers()
    {
        // Alice saves a task
        var aliceStore = new FileTaskStore(CreateAccessor("alice"), filePath: null);
        await aliceStore.SaveTaskAsync("t1", MakeTask("t1"), CancellationToken.None);

        // Bob saves a task (same store instance, different HTTP context)
        var bobAccessor = CreateAccessor("bob");
        // We need to share the same store but swap the accessor.
        // Since the store captures IHttpContextAccessor at construction, we use a shared store.
        // For this test, create a separate accessor-aware store by saving directly.
        // Simpler: use two stores that share the same file.
        var tempPath = Path.Combine(Path.GetTempPath(), $"task-test-{Guid.NewGuid():N}.json");
        try
        {
            var store1 = new FileTaskStore(CreateAccessor("alice"), tempPath);
            await store1.SaveTaskAsync("t-alice", MakeTask("t-alice"), CancellationToken.None);

            var store2 = new FileTaskStore(CreateAccessor("bob"), tempPath);
            await store2.SaveTaskAsync("t-bob", MakeTask("t-bob"), CancellationToken.None);

            // Alice's list should only show her task
            var aliceResult = await store2.ListTasksAsync(
                new ListTasksRequest { Tenant = "alice" }, CancellationToken.None);
            Assert.AreEqual(1, aliceResult.Tasks.Count);
            Assert.AreEqual("t-alice", aliceResult.Tasks[0].Id);

            // Bob's list should only show his task
            var bobResult = await store2.ListTasksAsync(
                new ListTasksRequest { Tenant = "bob" }, CancellationToken.None);
            Assert.AreEqual(1, bobResult.Tasks.Count);
            Assert.AreEqual("t-bob", bobResult.Tasks[0].Id);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [TestMethod]
    public async Task ListTasks_StatusFilter()
    {
        var store = new FileTaskStore(CreateAccessor("alice"), filePath: null);
        await store.SaveTaskAsync("t1", MakeTask("t1", TaskState.Completed), CancellationToken.None);
        await store.SaveTaskAsync("t2", MakeTask("t2", TaskState.Working), CancellationToken.None);
        await store.SaveTaskAsync("t3", MakeTask("t3", TaskState.Failed), CancellationToken.None);

        var result = await store.ListTasksAsync(
            new ListTasksRequest { Tenant = "alice", Status = TaskState.Working },
            CancellationToken.None);

        Assert.AreEqual(1, result.Tasks.Count);
        Assert.AreEqual("t2", result.Tasks[0].Id);
    }

    [TestMethod]
    public async Task ListTasks_Pagination()
    {
        var store = new FileTaskStore(CreateAccessor("alice"), filePath: null);
        for (int i = 1; i <= 5; i++)
            await store.SaveTaskAsync($"t{i}", MakeTask($"t{i}"), CancellationToken.None);

        var page1 = await store.ListTasksAsync(
            new ListTasksRequest { Tenant = "alice", PageSize = 2 },
            CancellationToken.None);

        Assert.AreEqual(2, page1.Tasks.Count);
        Assert.AreEqual(5, page1.TotalSize);
        Assert.IsFalse(string.IsNullOrEmpty(page1.NextPageToken));

        var page2 = await store.ListTasksAsync(
            new ListTasksRequest { Tenant = "alice", PageSize = 2, PageToken = page1.NextPageToken },
            CancellationToken.None);

        Assert.AreEqual(2, page2.Tasks.Count);
        // No overlap between pages
        Assert.IsFalse(page1.Tasks.Select(t => t.Id).Intersect(page2.Tasks.Select(t => t.Id)).Any());
    }

    [TestMethod]
    public async Task Persistence_SurvivesReload()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"task-test-{Guid.NewGuid():N}.json");
        try
        {
            // Save with one instance
            var store1 = new FileTaskStore(CreateAccessor("alice"), tempPath);
            await store1.SaveTaskAsync("t1", MakeTask("t1"), CancellationToken.None);

            // Load with a new instance
            var store2 = new FileTaskStore(CreateAccessor("alice"), tempPath);
            var task = await store2.GetTaskAsync("t1", CancellationToken.None);

            Assert.IsNotNull(task);
            Assert.AreEqual("t1", task!.Id);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [TestMethod]
    public async Task InMemoryMode_WorksWithoutFile()
    {
        var store = new FileTaskStore(CreateAccessor(), filePath: null);
        await store.SaveTaskAsync("t1", MakeTask("t1"), CancellationToken.None);

        var task = await store.GetTaskAsync("t1", CancellationToken.None);
        Assert.IsNotNull(task);
    }
}

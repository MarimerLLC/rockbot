using System.Text.Json;

namespace McpServer.TodoApp.Tests;

[TestClass]
public sealed class QueryTests : TodoServerTestBase
{
    private static string[] Titles(JsonElement list) =>
        list.EnumerateArray().Select(t => t.GetProperty("title").GetString()!).ToArray();

    private async Task SeedAsync()
    {
        // Inserted out of due-date order on purpose.
        await AddTaskAsync("Charlie", dueDate: "2026-12-01");
        await AddTaskAsync("alpha", dueDate: "2026-11-01");
        await CallOkAsync("add_task", new()
        {
            ["title"] = "Bravo",
            ["due_date"] = "2026-11-01",
            ["description"] = "Quarterly invoice for PWOP",
            ["recurrence"] = "monthly"
        });
    }

    [TestMethod]
    public async Task ListTasks_DefaultSort_ByDueDateThenTitle()
    {
        await SeedAsync();

        CollectionAssert.AreEqual(new[] { "alpha", "Bravo", "Charlie" }, Titles(await ListTasksAsync()));
    }

    [TestMethod]
    public async Task ListTasks_SortByTitleAndCreatedAt()
    {
        await SeedAsync();

        CollectionAssert.AreEqual(new[] { "alpha", "Bravo", "Charlie" },
            Titles(await CallOkAsync("list_tasks", new() { ["sort"] = "title" })));
        CollectionAssert.AreEqual(new[] { "Charlie", "alpha", "Bravo" },
            Titles(await CallOkAsync("list_tasks", new() { ["sort"] = "created_at" })));
    }

    [TestMethod]
    public async Task ListTasks_Query_MatchesTitleOrDescription()
    {
        await SeedAsync();

        CollectionAssert.AreEqual(new[] { "Charlie" }, Titles(await CallOkAsync("list_tasks", new() { ["query"] = "CHAR" })));
        CollectionAssert.AreEqual(new[] { "Bravo" }, Titles(await CallOkAsync("list_tasks", new() { ["query"] = "pwop" })));
        Assert.AreEqual(0, (await CallOkAsync("list_tasks", new() { ["query"] = "nothing" })).GetArrayLength());
    }

    [TestMethod]
    public async Task ListTasks_Compact_OnlyOverviewFields()
    {
        await SeedAsync();

        var first = (await CallOkAsync("list_tasks", new() { ["compact"] = true }))[0];

        CollectionAssert.AreEquivalent(
            new[] { "id", "title", "due_date", "recurrence" },
            first.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public async Task ListTasks_InvalidSort_NamesAllowedValues()
    {
        var result = await CallAsync("list_tasks", new() { ["sort"] = "priority" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "expected one of: due_date, created_at, title");
    }

    [TestMethod]
    public async Task GetTask_Active_ReturnsFullDetailWithNotes()
    {
        var id = await AddTaskAsync("Renew license");
        await CallOkAsync("add_task_note", new() { ["id"] = id, ["text"] = "checked" });

        var result = await CallOkAsync("get_task", new() { ["id"] = id });

        Assert.AreEqual("active", result.GetProperty("status").GetString());
        Assert.AreEqual(id, result.GetProperty("task").GetProperty("id").GetString());
        Assert.AreEqual("checked", result.GetProperty("task").GetProperty("notes")[0].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task GetTask_Completed_ReturnsCompletedRecord()
    {
        var id = await AddTaskAsync("One-off");
        await CallOkAsync("complete_task", new() { ["id"] = id });

        var result = await CallOkAsync("get_task", new() { ["id"] = id });

        Assert.AreEqual("completed", result.GetProperty("status").GetString());
        Assert.IsTrue(result.GetProperty("task").TryGetProperty("completed_at", out _));
    }

    [TestMethod]
    public async Task GetTask_Unknown_IsError()
    {
        var result = await CallAsync("get_task", new() { ["id"] = Guid.NewGuid().ToString() });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "task not found");
    }

    [TestMethod]
    public async Task ListCompleted_DefaultMostRecentFirst_QueryAndCompact()
    {
        foreach (var title in new[] { "First", "Second", "Third" })
            await CallOkAsync("complete_task", new() { ["id"] = await AddTaskAsync(title) });

        CollectionAssert.AreEqual(new[] { "Third", "Second", "First" }, Titles(await CallOkAsync("list_completed")));
        CollectionAssert.AreEqual(new[] { "Second" }, Titles(await CallOkAsync("list_completed", new() { ["query"] = "sec" })));

        var compact = (await CallOkAsync("list_completed", new() { ["compact"] = true }))[0];
        CollectionAssert.AreEquivalent(
            new[] { "id", "title", "due_date", "recurrence", "completed_at" },
            compact.EnumerateObject().Select(p => p.Name).ToArray());
    }
}

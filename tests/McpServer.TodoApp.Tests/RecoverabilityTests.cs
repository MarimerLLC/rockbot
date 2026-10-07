using System.Text.Json;

namespace McpServer.TodoApp.Tests;

[TestClass]
public sealed class RecoverabilityTests : TodoServerTestBase
{
    private async Task<JsonElement> CompleteAsync(string id) =>
        await CallOkAsync("complete_task", new() { ["id"] = id });

    private async Task<string[]> ActiveIdsAsync() =>
        (await ListTasksAsync()).EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToArray();

    [TestMethod]
    public async Task DeleteThenRestore_ReturnsIdenticalTask()
    {
        var id = await AddTaskAsync("Renew license", recurrence: "yearly");
        await CallOkAsync("add_task_note", new() { ["id"] = id, ["text"] = "keep me" });
        var before = (await CallOkAsync("get_task", new() { ["id"] = id })).GetProperty("task");

        var deleted = await CallOkAsync("delete_task", new() { ["id"] = id });
        Assert.AreEqual(id, deleted.GetProperty("id").GetString());
        Assert.IsTrue(deleted.TryGetProperty("purge_after", out _));
        Assert.AreEqual(0, (await ListTasksAsync()).GetArrayLength());

        var restored = await CallOkAsync("restore_task", new() { ["id"] = id });

        Assert.IsTrue(JsonElement.DeepEquals(before, restored), $"before: {before} restored: {restored}");
        CollectionAssert.AreEqual(new[] { id }, await ActiveIdsAsync());
        Assert.AreEqual(0, (await CallOkAsync("list_deleted")).GetArrayLength());
    }

    [TestMethod]
    public async Task ListDeleted_AndGetTask_ShowDeletedTask()
    {
        var id = await AddTaskAsync("Old idea");
        await CallOkAsync("delete_task", new() { ["id"] = id });

        var listed = await CallOkAsync("list_deleted");
        Assert.AreEqual(1, listed.GetArrayLength());
        Assert.AreEqual("Old idea", listed[0].GetProperty("title").GetString());
        Assert.IsTrue(listed[0].TryGetProperty("deleted_at", out _));

        var got = await CallOkAsync("get_task", new() { ["id"] = id });
        Assert.AreEqual("deleted", got.GetProperty("status").GetString());
        Assert.IsTrue(got.TryGetProperty("purge_after", out _));
    }

    [TestMethod]
    public async Task RestoreTask_NotDeleted_IsError()
    {
        var id = await AddTaskAsync("Active");

        var result = await CallAsync("restore_task", new() { ["id"] = id });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "deleted task not found");
    }

    [TestMethod]
    public async Task Purge_RemovesOnlyEntriesOlderThanRetention()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-31).ToString("O");
        var recent = DateTimeOffset.UtcNow.AddDays(-29).ToString("O");
        await File.WriteAllTextAsync(Path.Combine(DataPath, "deleted.json"), $$"""
            [
              { "task": { "id": "11111111-1111-1111-1111-111111111111", "title": "Expired", "dueDate": "2026-01-01", "recurrence": "none", "createdAt": "2026-01-01T00:00:00+00:00" }, "deletedAt": "{{old}}" },
              { "task": { "id": "22222222-2222-2222-2222-222222222222", "title": "Recent", "dueDate": "2026-01-01", "recurrence": "none", "createdAt": "2026-01-01T00:00:00+00:00" }, "deletedAt": "{{recent}}" }
            ]
            """);

        var listed = await CallOkAsync("list_deleted");

        Assert.AreEqual(1, listed.GetArrayLength());
        Assert.AreEqual("Recent", listed[0].GetProperty("title").GetString());
        var stored = await File.ReadAllTextAsync(Path.Combine(DataPath, "deleted.json"));
        Assert.IsFalse(stored.Contains("Expired"));
    }

    [TestMethod]
    public async Task Uncomplete_OneOff_RestoresTask()
    {
        var id = await AddTaskAsync("One-off");
        await CompleteAsync(id);

        var result = await CallOkAsync("uncomplete_task", new() { ["id"] = id });

        Assert.AreEqual(id, result.GetProperty("restored").GetProperty("id").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("removed_next").ValueKind);
        CollectionAssert.AreEqual(new[] { id }, await ActiveIdsAsync());
        Assert.AreEqual(0, (await CallOkAsync("list_completed")).GetArrayLength());
    }

    [TestMethod]
    public async Task Uncomplete_Recurring_RemovesUntouchedNextOccurrence()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Invoice",
            ["due_date"] = "2026-10-31",
            ["recurrence"] = "monthly",
            ["month_anchor"] = "last_day",
            ["recurrence_count"] = 12
        });
        var id = task.GetProperty("id").GetString()!;
        await CallOkAsync("add_task_note", new() { ["id"] = id, ["text"] = "sent" });
        var nextId = (await CompleteAsync(id)).GetProperty("next").GetProperty("id").GetString();

        var result = await CallOkAsync("uncomplete_task", new() { ["id"] = id });

        Assert.AreEqual(nextId, result.GetProperty("removed_next").GetString());
        var restored = result.GetProperty("restored");
        Assert.AreEqual("2026-10-31", restored.GetProperty("due_date").GetString());
        Assert.AreEqual("last_day", restored.GetProperty("month_anchor").GetString());
        Assert.AreEqual(12, restored.GetProperty("recurrence_count").GetInt32());
        Assert.AreEqual("sent", restored.GetProperty("notes")[0].GetProperty("text").GetString());
        CollectionAssert.AreEqual(new[] { id }, await ActiveIdsAsync());
    }

    [TestMethod]
    public async Task Uncomplete_NextEdited_IsErrorAndNothingChanges()
    {
        var id = await AddTaskAsync("Invoice", recurrence: "monthly");
        var nextId = (await CompleteAsync(id)).GetProperty("next").GetProperty("id").GetString()!;
        await CallOkAsync("update_task", new() { ["id"] = nextId, ["title"] = "Invoice (changed)" });

        var result = await CallAsync("uncomplete_task", new() { ["id"] = id });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), nextId);
        CollectionAssert.AreEqual(new[] { nextId }, await ActiveIdsAsync());
        Assert.AreEqual(1, (await CallOkAsync("list_completed")).GetArrayLength());
    }

    [TestMethod]
    public async Task Uncomplete_NextHasNote_IsError()
    {
        var id = await AddTaskAsync("Invoice", recurrence: "monthly");
        var nextId = (await CompleteAsync(id)).GetProperty("next").GetProperty("id").GetString()!;
        await CallOkAsync("add_task_note", new() { ["id"] = nextId, ["text"] = "work started" });

        var result = await CallAsync("uncomplete_task", new() { ["id"] = id });

        Assert.IsTrue(result.IsError);
    }

    [TestMethod]
    public async Task Uncomplete_NextAlreadyCompleted_IsError()
    {
        var id = await AddTaskAsync("Invoice", recurrence: "monthly");
        var nextId = (await CompleteAsync(id)).GetProperty("next").GetProperty("id").GetString()!;
        await CompleteAsync(nextId);

        var result = await CallAsync("uncomplete_task", new() { ["id"] = id });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "uncomplete that one first");
    }

    [TestMethod]
    public async Task Uncomplete_SeriesEnded_RestoresWithoutRemovingAnything()
    {
        var id = await AddTaskAsync("Club", recurrence: "monthly");
        await CallOkAsync("complete_task", new() { ["id"] = id, ["stop_recurrence"] = true });

        var result = await CallOkAsync("uncomplete_task", new() { ["id"] = id });

        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("removed_next").ValueKind);
        Assert.AreEqual("monthly", result.GetProperty("restored").GetProperty("recurrence").GetString());
    }

    [TestMethod]
    public async Task Uncomplete_CompletionRecordedWithoutSeriesFields_UsesSpawnedOccurrence()
    {
        // A completion written by an earlier version: no anchor/limits on the completed record,
        // while the spawned next occurrence carries them (anchor 31, clamped to Feb 28 -> next is Mar 31).
        const string series = "aaaaaaaa-0000-0000-0000-000000000001";
        const string doneId = "aaaaaaaa-0000-0000-0000-000000000002";
        const string nextId = "aaaaaaaa-0000-0000-0000-000000000003";
        await File.WriteAllTextAsync(Path.Combine(DataPath, "completed.json"), $$"""
            [ { "id": "{{doneId}}", "title": "Rent", "description": null, "dueDate": "2027-02-28", "recurrence": "monthly",
                "createdAt": "2027-01-01T00:00:00+00:00", "completedAt": "2027-02-27T00:00:00+00:00",
                "seriesId": "{{series}}", "occurrence": 2 } ]
            """);
        await File.WriteAllTextAsync(Path.Combine(DataPath, "active.json"), $$"""
            [ { "id": "{{nextId}}", "title": "Rent", "description": null, "dueDate": "2027-03-31", "recurrence": "monthly",
                "createdAt": "2027-02-27T00:00:00+00:00", "seriesId": "{{series}}", "occurrence": 3,
                "monthAnchor": "same_day", "anchorDay": 31 } ]
            """);

        var result = await CallOkAsync("uncomplete_task", new() { ["id"] = doneId });

        Assert.AreEqual(nextId, result.GetProperty("removed_next").GetString());
        Assert.AreEqual(31, result.GetProperty("restored").GetProperty("anchor_day").GetInt32());
        CollectionAssert.AreEqual(new[] { doneId }, await ActiveIdsAsync());
    }

    [TestMethod]
    public async Task Uncomplete_Unknown_IsError()
    {
        var result = await CallAsync("uncomplete_task", new() { ["id"] = Guid.NewGuid().ToString() });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "completed task not found");
    }
}

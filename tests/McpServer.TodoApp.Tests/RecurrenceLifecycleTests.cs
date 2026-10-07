using System.Text.Json;

namespace McpServer.TodoApp.Tests;

[TestClass]
public sealed class RecurrenceLifecycleTests : TodoServerTestBase
{
    [TestMethod]
    public async Task UpdateTask_SetsRecurrence_KeepsId()
    {
        var id = await AddTaskAsync("Water plants");

        var updated = await CallOkAsync("update_task", new() { ["id"] = id, ["recurrence"] = "weekly" });

        Assert.AreEqual(id, updated.GetProperty("id").GetString());
        Assert.AreEqual("weekly", updated.GetProperty("recurrence").GetString());
    }

    [TestMethod]
    public async Task UpdateTask_RecurrenceNone_EndsSeriesOnCompletion()
    {
        var id = await AddTaskAsync("Club dues", recurrence: "monthly");

        await CallOkAsync("update_task", new() { ["id"] = id, ["recurrence"] = "none" });
        var result = await CallOkAsync("complete_task", new() { ["id"] = id });

        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("next").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("series_ended").ValueKind);
        Assert.AreEqual(0, (await ListTasksAsync()).GetArrayLength());
    }

    [TestMethod]
    public async Task CompleteTask_Recurring_ReturnsNextOccurrence()
    {
        var id = await AddTaskAsync("Invoice", dueDate: "2026-10-15", recurrence: "monthly");

        var result = await CallOkAsync("complete_task", new() { ["id"] = id });

        var completed = result.GetProperty("completed");
        var next = result.GetProperty("next");
        Assert.AreEqual(id, completed.GetProperty("id").GetString());
        Assert.AreNotEqual(id, next.GetProperty("id").GetString());
        Assert.AreEqual(completed.GetProperty("series_id").GetString(), next.GetProperty("series_id").GetString());
        Assert.AreEqual(id, next.GetProperty("series_id").GetString());
        Assert.AreEqual("2026-11-15", next.GetProperty("due_date").GetString());
        Assert.AreEqual(2, next.GetProperty("occurrence").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("series_ended").ValueKind);

        var tasks = await ListTasksAsync();
        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual(next.GetProperty("id").GetString(), tasks[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task CompleteTask_NonRecurring_NextIsNull()
    {
        var id = await AddTaskAsync("One-off");

        var result = await CallOkAsync("complete_task", new() { ["id"] = id });

        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("next").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("series_ended").ValueKind);
    }

    [TestMethod]
    public async Task CompleteTask_StopRecurrence_EndsSeries()
    {
        var id = await AddTaskAsync("Rock-it club", recurrence: "monthly");

        var result = await CallOkAsync("complete_task", new() { ["id"] = id, ["stop_recurrence"] = true });

        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("next").ValueKind);
        Assert.AreEqual("stop_recurrence", result.GetProperty("series_ended").GetString());
        Assert.AreEqual(0, (await ListTasksAsync()).GetArrayLength());
    }

    [TestMethod]
    public async Task CompleteTask_RecurrenceCount_StopsAfterLastOccurrence()
    {
        var first = await CallOkAsync("add_task", new()
        {
            ["title"] = "Physio",
            ["due_date"] = "2026-10-01",
            ["recurrence"] = "weekly",
            ["recurrence_count"] = 2
        });

        var r1 = await CallOkAsync("complete_task", new() { ["id"] = first.GetProperty("id").GetString() });
        var second = r1.GetProperty("next");
        Assert.AreEqual(2, second.GetProperty("occurrence").GetInt32());

        var r2 = await CallOkAsync("complete_task", new() { ["id"] = second.GetProperty("id").GetString() });
        Assert.AreEqual(JsonValueKind.Null, r2.GetProperty("next").ValueKind);
        Assert.AreEqual("count_reached", r2.GetProperty("series_ended").GetString());
        Assert.AreEqual(0, (await ListTasksAsync()).GetArrayLength());
    }

    [TestMethod]
    public async Task CompleteTask_RecurrenceUntil_IsInclusive()
    {
        var first = await CallOkAsync("add_task", new()
        {
            ["title"] = "Standup",
            ["due_date"] = "2026-10-01",
            ["recurrence"] = "weekly",
            ["recurrence_until"] = "2026-10-08"
        });

        var r1 = await CallOkAsync("complete_task", new() { ["id"] = first.GetProperty("id").GetString() });
        var second = r1.GetProperty("next");
        Assert.AreEqual("2026-10-08", second.GetProperty("due_date").GetString());

        var r2 = await CallOkAsync("complete_task", new() { ["id"] = second.GetProperty("id").GetString() });
        Assert.AreEqual(JsonValueKind.Null, r2.GetProperty("next").ValueKind);
        Assert.AreEqual("until_reached", r2.GetProperty("series_ended").GetString());
    }

    [TestMethod]
    public async Task UpdateTask_ClearsLimits()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Physio",
            ["due_date"] = "2026-10-01",
            ["recurrence"] = "weekly",
            ["recurrence_count"] = 1,
            ["recurrence_until"] = "2026-10-01"
        });
        var id = task.GetProperty("id").GetString();

        var updated = await CallOkAsync("update_task", new()
        {
            ["id"] = id,
            ["recurrence_count"] = 0,
            ["recurrence_until"] = ""
        });
        Assert.AreEqual(JsonValueKind.Null, updated.GetProperty("recurrence_count").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, updated.GetProperty("recurrence_until").ValueKind);

        var result = await CallOkAsync("complete_task", new() { ["id"] = id });
        Assert.AreEqual(JsonValueKind.Object, result.GetProperty("next").ValueKind);
    }

    [TestMethod]
    public async Task UpdateTask_SetsLimitsOnExistingSeries()
    {
        var id = await AddTaskAsync("Standup", dueDate: "2026-10-01", recurrence: "weekly");

        await CallOkAsync("update_task", new() { ["id"] = id, ["recurrence_count"] = 1 });
        var result = await CallOkAsync("complete_task", new() { ["id"] = id });

        Assert.AreEqual("count_reached", result.GetProperty("series_ended").GetString());
    }

    [TestMethod]
    public async Task UpdateTask_RecurrenceNone_DropsExistingLimits()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Physio",
            ["due_date"] = "2026-10-01",
            ["recurrence"] = "weekly",
            ["recurrence_count"] = 3
        });

        var updated = await CallOkAsync("update_task", new()
        {
            ["id"] = task.GetProperty("id").GetString(),
            ["recurrence"] = "none"
        });

        Assert.AreEqual("none", updated.GetProperty("recurrence").GetString());
        Assert.AreEqual(JsonValueKind.Null, updated.GetProperty("recurrence_count").ValueKind);
    }

    [TestMethod]
    public async Task AddTask_LimitWithoutRecurrence_IsError()
    {
        var result = await CallAsync("add_task", new()
        {
            ["title"] = "One-off",
            ["due_date"] = "2026-10-01",
            ["recurrence_count"] = 3
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "require a recurrence");
        Assert.AreEqual(0, (await ListTasksAsync()).GetArrayLength());
    }

    [TestMethod]
    public async Task UpdateTask_LimitWithoutRecurrence_IsError()
    {
        var id = await AddTaskAsync("One-off");

        var result = await CallAsync("update_task", new() { ["id"] = id, ["recurrence_until"] = "2027-01-01" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "require a recurrence");
    }

    [TestMethod]
    public async Task UpdateTask_NegativeCount_IsError()
    {
        var id = await AddTaskAsync("Standup", recurrence: "weekly");

        var result = await CallAsync("update_task", new() { ["id"] = id, ["recurrence_count"] = -1 });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "recurrence_count");
    }

    [TestMethod]
    public async Task AddTask_ZeroCount_IsError()
    {
        var result = await CallAsync("add_task", new()
        {
            ["title"] = "Standup",
            ["due_date"] = "2026-10-01",
            ["recurrence"] = "weekly",
            ["recurrence_count"] = 0
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "recurrence_count");
    }

    [TestMethod]
    public async Task AddTask_InvalidUntil_IsError()
    {
        var result = await CallAsync("add_task", new()
        {
            ["title"] = "Standup",
            ["due_date"] = "2026-10-01",
            ["recurrence"] = "weekly",
            ["recurrence_until"] = "someday"
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "recurrence_until");
    }

    [TestMethod]
    public async Task LegacyData_LoadsWithSeriesIdAndCompletes()
    {
        const string legacyId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        await File.WriteAllTextAsync(Path.Combine(DataPath, "active.json"), $$"""
            [
              {
                "id": "{{legacyId}}",
                "title": "Legacy invoice",
                "description": null,
                "dueDate": "2026-10-31",
                "recurrence": "biAnnual",
                "createdAt": "2026-01-01T00:00:00+00:00"
              }
            ]
            """);

        var tasks = await ListTasksAsync();
        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual(legacyId, tasks[0].GetProperty("series_id").GetString());
        Assert.AreEqual(1, tasks[0].GetProperty("occurrence").GetInt32());

        var result = await CallOkAsync("complete_task", new() { ["id"] = legacyId });
        var next = result.GetProperty("next");
        Assert.AreEqual(legacyId, next.GetProperty("series_id").GetString());
        Assert.AreEqual("2027-04-30", next.GetProperty("due_date").GetString());
    }
}

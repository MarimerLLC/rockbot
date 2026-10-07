using System.Text.Json;

namespace McpServer.TodoApp.Tests;

[TestClass]
public sealed class MonthAnchorTests : TodoServerTestBase
{
    private async Task<JsonElement> CompleteAsync(string id) =>
        (await CallOkAsync("complete_task", new() { ["id"] = id })).GetProperty("next");

    private static string Due(JsonElement task) => task.GetProperty("dueDate").GetString()!;
    private static string Id(JsonElement task) => task.GetProperty("id").GetString()!;

    [TestMethod]
    public async Task AddTask_LastDay_SnapsAndRepeatsAtMonthEnd()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Invoice",
            ["due_date"] = "2026-10-15",
            ["recurrence"] = "monthly",
            ["month_anchor"] = "last_day"
        });
        Assert.AreEqual("2026-10-31", Due(task));
        Assert.AreEqual("last_day", task.GetProperty("monthAnchor").GetString());

        var next = await CompleteAsync(Id(task));
        Assert.AreEqual("2026-11-30", Due(next));
        Assert.AreEqual("last_day", next.GetProperty("monthAnchor").GetString());
    }

    [TestMethod]
    public async Task AddTask_SameDay31_DoesNotDriftAcrossCompletions()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Rent",
            ["due_date"] = "2027-01-31",
            ["recurrence"] = "monthly"
        });
        Assert.AreEqual("same_day", task.GetProperty("monthAnchor").GetString());
        Assert.AreEqual(31, task.GetProperty("anchorDay").GetInt32());

        var feb = await CompleteAsync(Id(task));
        var mar = await CompleteAsync(Id(feb));
        var apr = await CompleteAsync(Id(mar));

        CollectionAssert.AreEqual(
            new[] { "2027-02-28", "2027-03-31", "2027-04-30" },
            new[] { Due(feb), Due(mar), Due(apr) });
    }

    [TestMethod]
    public async Task UpdateTask_TitleOnClampedOccurrence_KeepsAnchorDay()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Rent",
            ["due_date"] = "2027-01-31",
            ["recurrence"] = "monthly"
        });
        var feb = await CompleteAsync(Id(task));

        var renamed = await CallOkAsync("update_task", new() { ["id"] = Id(feb), ["title"] = "Rent (renamed)" });
        Assert.AreEqual(31, renamed.GetProperty("anchorDay").GetInt32());

        var mar = await CompleteAsync(Id(feb));
        Assert.AreEqual("2027-03-31", Due(mar));
    }

    [TestMethod]
    public async Task UpdateTask_SwitchToLastDay_SnapsCurrentDueDate()
    {
        var id = await AddTaskAsync("PWOP invoice", dueDate: "2026-10-30", recurrence: "monthly");

        var updated = await CallOkAsync("update_task", new() { ["id"] = id, ["month_anchor"] = "last_day" });
        Assert.AreEqual("2026-10-31", Due(updated));
        Assert.AreEqual(id, Id(updated));

        var next = await CompleteAsync(id);
        Assert.AreEqual("2026-11-30", Due(next));
    }

    [TestMethod]
    public async Task UpdateTask_LastDayWithExplicitDueDate_KeepsDueDate()
    {
        var id = await AddTaskAsync("Invoice", dueDate: "2026-10-30", recurrence: "monthly");

        var updated = await CallOkAsync("update_task", new()
        {
            ["id"] = id,
            ["month_anchor"] = "last_day",
            ["due_date"] = "2026-11-15"
        });

        Assert.AreEqual("2026-11-15", Due(updated));
        Assert.AreEqual("2026-12-31", Due(await CompleteAsync(id)));
    }

    [TestMethod]
    public async Task UpdateTask_MovingDueDate_MovesSameDayAnchor()
    {
        var id = await AddTaskAsync("Rent", dueDate: "2026-10-31", recurrence: "monthly");

        var updated = await CallOkAsync("update_task", new() { ["id"] = id, ["due_date"] = "2026-11-15" });

        Assert.AreEqual(15, updated.GetProperty("anchorDay").GetInt32());
        Assert.AreEqual("2026-12-15", Due(await CompleteAsync(id)));
    }

    [TestMethod]
    public async Task UpdateTask_ToWeekly_ClearsAnchor()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Invoice",
            ["due_date"] = "2026-10-31",
            ["recurrence"] = "monthly",
            ["month_anchor"] = "last_day"
        });

        var updated = await CallOkAsync("update_task", new() { ["id"] = Id(task), ["recurrence"] = "weekly" });

        Assert.AreEqual(JsonValueKind.Null, updated.GetProperty("monthAnchor").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, updated.GetProperty("anchorDay").ValueKind);
    }

    [TestMethod]
    public async Task MonthAnchor_WithWeekly_IsError()
    {
        var result = await CallAsync("add_task", new()
        {
            ["title"] = "Standup",
            ["due_date"] = "2026-10-01",
            ["recurrence"] = "weekly",
            ["month_anchor"] = "last_day"
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "month_anchor");
    }

    [TestMethod]
    public async Task MonthAnchor_Invalid_IsError()
    {
        var id = await AddTaskAsync("Invoice", recurrence: "monthly");

        var result = await CallAsync("update_task", new() { ["id"] = id, ["month_anchor"] = "end" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "same_day or last_day");
    }

    [TestMethod]
    public async Task LegacyMonthlyOn31st_NextOccurrenceUsesDueDay()
    {
        const string legacyId = "6fa459ea-ee8a-3ca4-894e-db77e160355e";
        await File.WriteAllTextAsync(Path.Combine(DataPath, "active.json"), $$"""
            [
              {
                "id": "{{legacyId}}",
                "title": "Legacy month-end",
                "description": null,
                "dueDate": "2026-12-31",
                "recurrence": "monthly",
                "createdAt": "2026-01-01T00:00:00+00:00"
              }
            ]
            """);

        var jan = await CompleteAsync(legacyId);
        Assert.AreEqual("2027-01-31", Due(jan));
    }
}

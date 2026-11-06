using System.Text.Json;

namespace McpServer.TodoApp.Tests;

/// <summary>End-to-end coverage of the biweekly and semimonthly (month_days) recurrences.</summary>
[TestClass]
public sealed class SemiMonthlyTests : TodoServerTestBase
{
    private async Task<JsonElement> CompleteAsync(string id) =>
        (await CallOkAsync("complete_task", new() { ["id"] = id })).GetProperty("next");

    private static string Due(JsonElement task) => task.GetProperty("due_date").GetString()!;
    private static string Id(JsonElement task) => task.GetProperty("id").GetString()!;

    private static int[] MonthDays(JsonElement task) =>
        task.GetProperty("month_days").ValueKind == JsonValueKind.Null
            ? []
            : task.GetProperty("month_days").EnumerateArray().Select(d => d.GetInt32()).ToArray();

    private Task<JsonElement> AddSemiMonthlyAsync(string dueDate, int[]? monthDays = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["title"] = "Payroll",
            ["due_date"] = dueDate,
            ["recurrence"] = "semimonthly"
        };
        if (monthDays is not null)
            args["month_days"] = monthDays;
        return CallOkAsync("add_task", args);
    }

    [TestMethod]
    public async Task BiWeekly_CompleteCreatesNextFourteenDaysLater()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Sprint review",
            ["due_date"] = "2026-11-13",
            ["recurrence"] = "biweekly"
        });
        Assert.AreEqual("biweekly", task.GetProperty("recurrence").GetString());

        var next = await CompleteAsync(Id(task));

        Assert.AreEqual("2026-11-27", Due(next));
        Assert.AreEqual("biweekly", next.GetProperty("recurrence").GetString());
    }

    [TestMethod]
    public async Task SemiMonthly_FifteenthAndMonthEnd_ChainsAcrossShortMonths()
    {
        var task = await AddSemiMonthlyAsync("2027-01-15", [15, 31]);
        CollectionAssert.AreEqual(new[] { 15, 31 }, MonthDays(task));

        var dates = new List<string>();
        var current = task;
        for (var i = 0; i < 4; i++)
        {
            current = await CompleteAsync(Id(current));
            dates.Add(Due(current));
        }

        CollectionAssert.AreEqual(new[] { "2027-01-31", "2027-02-15", "2027-02-28", "2027-03-15" }, dates);
        CollectionAssert.AreEqual(new[] { 15, 31 }, MonthDays(current));
    }

    [TestMethod]
    public async Task SemiMonthly_DaysAreSorted()
    {
        var task = await AddSemiMonthlyAsync("2026-12-01", [15, 1]);

        CollectionAssert.AreEqual(new[] { 1, 15 }, MonthDays(task));
        Assert.AreEqual("2026-12-15", Due(await CompleteAsync(Id(task))));
    }

    [TestMethod]
    public async Task SemiMonthly_DefaultsToFifteenthAndMonthEnd_AndSnapsDueDateForward()
    {
        var task = await AddSemiMonthlyAsync("2026-11-06");

        CollectionAssert.AreEqual(new[] { 15, 31 }, MonthDays(task));
        Assert.AreEqual("2026-11-15", Due(task));
    }

    [TestMethod]
    [DataRow(new[] { 15 }, DisplayName = "one day")]
    [DataRow(new[] { 1, 15, 28 }, DisplayName = "three days")]
    [DataRow(new[] { 15, 15 }, DisplayName = "duplicate")]
    [DataRow(new[] { 0, 15 }, DisplayName = "zero")]
    [DataRow(new[] { 15, 32 }, DisplayName = "past 31")]
    public async Task MonthDays_Invalid_IsError(int[] monthDays)
    {
        var result = await CallAsync("add_task", new()
        {
            ["title"] = "Payroll",
            ["due_date"] = "2026-11-15",
            ["recurrence"] = "semimonthly",
            ["month_days"] = monthDays
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "month_days");
    }

    [TestMethod]
    public async Task MonthDays_WithMonthly_IsError()
    {
        var result = await CallAsync("add_task", new()
        {
            ["title"] = "Rent",
            ["due_date"] = "2026-11-15",
            ["recurrence"] = "monthly",
            ["month_days"] = new[] { 1, 15 }
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "month_days requires a semimonthly recurrence");
    }

    [TestMethod]
    public async Task MonthAnchor_WithSemiMonthly_IsError()
    {
        var result = await CallAsync("add_task", new()
        {
            ["title"] = "Payroll",
            ["due_date"] = "2026-11-15",
            ["recurrence"] = "semimonthly",
            ["month_anchor"] = "last_day"
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "month_anchor");
    }

    [TestMethod]
    public async Task UpdateTask_MonthlyToSemiMonthly_SetsDaysAndSnapsDueDate()
    {
        var id = await AddTaskAsync("Payroll", "2026-11-10", "monthly");

        var updated = await CallOkAsync("update_task", new()
        {
            ["id"] = id,
            ["recurrence"] = "semimonthly",
            ["month_days"] = new[] { 1, 15 }
        });

        Assert.AreEqual("semimonthly", updated.GetProperty("recurrence").GetString());
        CollectionAssert.AreEqual(new[] { 1, 15 }, MonthDays(updated));
        Assert.AreEqual("2026-11-15", Due(updated));
        Assert.AreEqual(JsonValueKind.Null, updated.GetProperty("month_anchor").ValueKind);
    }

    [TestMethod]
    public async Task UpdateTask_TitleOnly_KeepsMonthDaysAndDueDate()
    {
        var task = await AddSemiMonthlyAsync("2027-01-15", [15, 31]);
        var feb = await CompleteAsync(Id(await CompleteAsync(Id(task)))); // 2027-02-15
        var febEnd = await CompleteAsync(Id(feb));                         // 2027-02-28, clamped from 31

        var updated = await CallOkAsync("update_task", new() { ["id"] = Id(febEnd), ["title"] = "Payroll run" });

        Assert.AreEqual("2027-02-28", Due(updated));
        CollectionAssert.AreEqual(new[] { 15, 31 }, MonthDays(updated));
    }

    [TestMethod]
    public async Task UpdateTask_SemiMonthlyToWeekly_ClearsMonthDays()
    {
        var task = await AddSemiMonthlyAsync("2026-11-15", [15, 31]);

        var updated = await CallOkAsync("update_task", new() { ["id"] = Id(task), ["recurrence"] = "weekly" });

        Assert.AreEqual(JsonValueKind.Null, updated.GetProperty("month_days").ValueKind);
        Assert.AreEqual("2026-11-15", Due(updated));
    }

    [TestMethod]
    public async Task UpdateTask_MonthDaysOnWeekly_IsError()
    {
        var id = await AddTaskAsync("Standup", "2026-11-15", "weekly");

        var result = await CallAsync("update_task", new() { ["id"] = id, ["month_days"] = new[] { 1, 15 } });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "month_days");
    }

    [TestMethod]
    public async Task UncompleteTask_RestoresMonthDaysAndRemovesSpawnedNext()
    {
        var task = await AddSemiMonthlyAsync("2026-11-15", [15, 31]);
        var next = await CompleteAsync(Id(task));

        var result = await CallOkAsync("uncomplete_task", new() { ["id"] = Id(task) });

        Assert.AreEqual(Id(next), result.GetProperty("removed_next").GetString());
        CollectionAssert.AreEqual(new[] { 15, 31 }, MonthDays(result.GetProperty("restored")));
        var active = await ListTasksAsync();
        Assert.AreEqual(1, active.GetArrayLength());
    }

    [TestMethod]
    public async Task SemiMonthly_RecurrenceCount_EndsSeries()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Payroll",
            ["due_date"] = "2026-11-15",
            ["recurrence"] = "semimonthly",
            ["recurrence_count"] = 2
        });

        var second = await CompleteAsync(Id(task));
        Assert.AreEqual("2026-11-30", Due(second));

        var last = await CallOkAsync("complete_task", new() { ["id"] = Id(second) });
        Assert.AreEqual(JsonValueKind.Null, last.GetProperty("next").ValueKind);
        Assert.AreEqual("count_reached", last.GetProperty("series_ended").GetString());
    }
}

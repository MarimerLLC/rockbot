using McpServer.TodoApp.Models;
using McpServer.TodoApp.Services;

namespace McpServer.TodoApp.Tests;

[TestClass]
public sealed class RecurrenceTests
{
    private static TodoItem Task(string due, RecurrenceType recurrence, MonthAnchor? anchor = null, int? anchorDay = null) =>
        new(Guid.NewGuid(), "t", null, DateOnly.Parse(due), recurrence, DateTimeOffset.UtcNow,
            MonthAnchor: anchor, AnchorDay: anchorDay);

    /// <summary>Feeds each computed due date back in, the way complete_task chains occurrences.</summary>
    private static string[] Chain(TodoItem first, int count)
    {
        var dates = new List<string>();
        var current = first;
        for (var i = 0; i < count; i++)
        {
            current = current with { DueDate = Recurrence.NextDueDate(current) };
            dates.Add(current.DueDate.ToString("yyyy-MM-dd"));
        }
        return [.. dates];
    }

    [TestMethod]
    public void Monthly_SameDay31_DoesNotDrift()
    {
        var dates = Chain(Task("2027-01-31", RecurrenceType.Monthly, MonthAnchor.SameDay, 31), 4);

        CollectionAssert.AreEqual(new[] { "2027-02-28", "2027-03-31", "2027-04-30", "2027-05-31" }, dates);
    }

    [TestMethod]
    public void Monthly_SameDay31_LeapFebruary()
    {
        var dates = Chain(Task("2028-01-31", RecurrenceType.Monthly, MonthAnchor.SameDay, 31), 2);

        CollectionAssert.AreEqual(new[] { "2028-02-29", "2028-03-31" }, dates);
    }

    [TestMethod]
    public void Monthly_LastDay_AlwaysMonthEnd()
    {
        var dates = Chain(Task("2026-10-31", RecurrenceType.Monthly, MonthAnchor.LastDay), 5);

        CollectionAssert.AreEqual(new[] { "2026-11-30", "2026-12-31", "2027-01-31", "2027-02-28", "2027-03-31" }, dates);
    }

    [TestMethod]
    public void Quarterly_SameDay30_ReturnsAfterFebruary()
    {
        var dates = Chain(Task("2026-11-30", RecurrenceType.Quarterly, MonthAnchor.SameDay, 30), 2);

        CollectionAssert.AreEqual(new[] { "2027-02-28", "2027-05-30" }, dates);
    }

    [TestMethod]
    public void BiAnnual_LastDay()
    {
        var dates = Chain(Task("2026-08-31", RecurrenceType.BiAnnual, MonthAnchor.LastDay), 2);

        CollectionAssert.AreEqual(new[] { "2027-02-28", "2027-08-31" }, dates);
    }

    [TestMethod]
    public void Yearly_Feb29_ReturnsInLeapYear()
    {
        var dates = Chain(Task("2028-02-29", RecurrenceType.Yearly, MonthAnchor.SameDay, 29), 4);

        CollectionAssert.AreEqual(new[] { "2029-02-28", "2030-02-28", "2031-02-28", "2032-02-29" }, dates);
    }

    [TestMethod]
    public void Legacy_NoAnchor_UsesDueDay()
    {
        var next = Recurrence.NextDueDate(Task("2026-10-31", RecurrenceType.Monthly));

        Assert.AreEqual(new DateOnly(2026, 11, 30), next);
    }

    [TestMethod]
    public void DailyAndWeekly_Unchanged()
    {
        Assert.AreEqual(new DateOnly(2026, 11, 1), Recurrence.NextDueDate(Task("2026-10-31", RecurrenceType.Daily)));
        Assert.AreEqual(new DateOnly(2026, 11, 7), Recurrence.NextDueDate(Task("2026-10-31", RecurrenceType.Weekly)));
    }

    [TestMethod]
    public void EndOfMonth_HandlesLeapYears()
    {
        Assert.AreEqual(new DateOnly(2028, 2, 29), Recurrence.EndOfMonth(new DateOnly(2028, 2, 3)));
        Assert.AreEqual(new DateOnly(2027, 2, 28), Recurrence.EndOfMonth(new DateOnly(2027, 2, 3)));
    }
}

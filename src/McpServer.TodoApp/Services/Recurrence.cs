using McpServer.TodoApp.Models;

namespace McpServer.TodoApp.Services;

public static class Recurrence
{
    /// <summary>True for recurrences that step by months and therefore need a day-of-month anchor.</summary>
    public static bool IsMonthBased(RecurrenceType recurrence) => recurrence is
        RecurrenceType.Monthly or RecurrenceType.Quarterly or RecurrenceType.BiAnnual or RecurrenceType.Yearly;

    public static DateOnly EndOfMonth(DateOnly date) =>
        new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));

    /// <summary>
    /// Computes the due date of the occurrence after <paramref name="task"/>. Month-based recurrences are
    /// computed from the series' anchor day, not the previous due date, so a 31st that was clamped to the
    /// 28th in February returns to the 31st in March.
    /// </summary>
    public static DateOnly NextDueDate(TodoItem task)
    {
        var months = task.Recurrence switch
        {
            RecurrenceType.Daily => 0,
            RecurrenceType.Weekly => 0,
            RecurrenceType.Monthly => 1,
            RecurrenceType.Quarterly => 3,
            RecurrenceType.BiAnnual => 6,
            RecurrenceType.Yearly => 12,
            _ => 0
        };

        if (months == 0)
        {
            return task.Recurrence switch
            {
                RecurrenceType.Daily => task.DueDate.AddDays(1),
                RecurrenceType.Weekly => task.DueDate.AddDays(7),
                _ => task.DueDate
            };
        }

        // Step on the 1st so the month arithmetic itself never clamps, then apply the anchor.
        var month = new DateOnly(task.DueDate.Year, task.DueDate.Month, 1).AddMonths(months);
        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
        var day = task.MonthAnchor == MonthAnchor.LastDay
            ? daysInMonth
            : Math.Min(task.AnchorDay ?? task.DueDate.Day, daysInMonth);
        return new DateOnly(month.Year, month.Month, day);
    }
}

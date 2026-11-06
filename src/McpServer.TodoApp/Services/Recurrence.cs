using McpServer.TodoApp.Models;

namespace McpServer.TodoApp.Services;

public static class Recurrence
{
    /// <summary>True for recurrences that step by months and therefore need a day-of-month anchor.</summary>
    public static bool IsMonthBased(RecurrenceType recurrence) => recurrence is
        RecurrenceType.Monthly or RecurrenceType.Quarterly or RecurrenceType.BiAnnual or RecurrenceType.Yearly;

    public static DateOnly EndOfMonth(DateOnly date) =>
        new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));

    /// <summary>The days a semimonthly series uses when none are given: the 15th and the last day of the month.</summary>
    public static IReadOnlyList<int> DefaultMonthDays { get; } = [15, 31];

    /// <summary>
    /// The first date on or after <paramref name="date"/> that falls on one of <paramref name="monthDays"/>. A day
    /// past the end of a month lands on that month's last day, so 31 means "end of month".
    /// </summary>
    public static DateOnly NextOnOrAfter(DateOnly date, IReadOnlyList<int> monthDays)
    {
        if (monthDays.Count == 0)
            throw new ArgumentException("at least one day of the month is required", nameof(monthDays));

        var month = new DateOnly(date.Year, date.Month, 1);
        // Every day is at most 31, so a match is always in this month or the next one.
        for (var i = 0; i < 2; i++, month = month.AddMonths(1))
        {
            var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
            var candidates = monthDays
                .Select(d => new DateOnly(month.Year, month.Month, Math.Min(d, daysInMonth)))
                .Where(d => d >= date)
                .ToList();
            if (candidates.Count > 0)
                return candidates.Min();
        }

        throw new InvalidOperationException("unreachable: no matching day in two consecutive months");
    }

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
                RecurrenceType.BiWeekly => task.DueDate.AddDays(14),
                RecurrenceType.SemiMonthly => NextOnOrAfter(task.DueDate.AddDays(1), task.MonthDays ?? DefaultMonthDays),
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

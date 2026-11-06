namespace McpServer.TodoApp.Models;

/// <summary>
/// How a task repeats. <see cref="BiWeekly"/> is every 14 days; <see cref="SemiMonthly"/> is twice a month on the
/// series' two <c>MonthDays</c>.
/// </summary>
public enum RecurrenceType { None, Daily, Weekly, BiWeekly, SemiMonthly, Monthly, Quarterly, BiAnnual, Yearly }

using System.Text.Json.Serialization;

namespace McpServer.TodoApp.Models;

/// <summary>Which day of the month a monthly, quarterly, biannual or yearly series lands on.</summary>
public enum MonthAnchor
{
    /// <summary>The series' original day of the month, clamped to the month's length.</summary>
    [JsonStringEnumMemberName("same_day")] SameDay,

    /// <summary>The last day of every month.</summary>
    [JsonStringEnumMemberName("last_day")] LastDay
}

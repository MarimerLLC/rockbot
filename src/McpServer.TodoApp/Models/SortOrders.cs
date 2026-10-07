using System.Text.Json.Serialization;

namespace McpServer.TodoApp.Models;

/// <summary>Sort order for list_tasks.</summary>
public enum TaskSort
{
    [JsonStringEnumMemberName("due_date")] DueDate,
    [JsonStringEnumMemberName("created_at")] CreatedAt,
    [JsonStringEnumMemberName("title")] Title
}

/// <summary>Sort order for list_completed.</summary>
public enum CompletedSort
{
    [JsonStringEnumMemberName("completed_at")] CompletedAt,
    [JsonStringEnumMemberName("due_date")] DueDate,
    [JsonStringEnumMemberName("title")] Title
}

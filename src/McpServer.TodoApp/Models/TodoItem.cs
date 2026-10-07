namespace McpServer.TodoApp.Models;

public sealed record TodoItem(
    Guid Id,
    string Title,
    string? Description,
    DateOnly DueDate,
    RecurrenceType Recurrence,
    DateTimeOffset CreatedAt,
    Guid? SeriesId = null,
    DateOnly? RecurrenceUntil = null,
    int? RecurrenceCount = null,
    int Occurrence = 1,
    MonthAnchor? MonthAnchor = null,
    int? AnchorDay = null
);

public sealed record CompletedTodoItem(
    Guid Id,
    string Title,
    string? Description,
    DateOnly DueDate,
    RecurrenceType Recurrence,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt,
    Guid? SeriesId = null,
    int Occurrence = 1
);

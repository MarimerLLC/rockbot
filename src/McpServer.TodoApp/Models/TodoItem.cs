namespace McpServer.TodoApp.Models;

public sealed record TodoItem(
    Guid Id,
    string Title,
    string? Description,
    DateOnly DueDate,
    RecurrenceType Recurrence,
    DateTimeOffset CreatedAt
);

public sealed record CompletedTodoItem(
    Guid Id,
    string Title,
    string? Description,
    DateOnly DueDate,
    RecurrenceType Recurrence,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt
);

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
    int? AnchorDay = null,
    IReadOnlyList<TaskNote>? Notes = null
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
    int Occurrence = 1,
    IReadOnlyList<TaskNote>? Notes = null,
    DateOnly? RecurrenceUntil = null,
    int? RecurrenceCount = null,
    MonthAnchor? MonthAnchor = null,
    int? AnchorDay = null
);

/// <summary>A soft-deleted task, restorable until the retention window passes.</summary>
public sealed record DeletedTodoItem(TodoItem Task, DateTimeOffset DeletedAt);

/// <summary>An append-only status or activity entry, kept separate from the task's description.</summary>
public sealed record TaskNote(DateTimeOffset At, string Text, string? Source = null);

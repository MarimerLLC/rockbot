using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpServer.TodoApp.Models;
using McpServer.TodoApp.Services;
using ModelContextProtocol.Server;

namespace McpServer.TodoApp.Tools;

[McpServerToolType]
public sealed class TodoTools(TodoRepository repository)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [McpServerTool(Name = "add_task")]
    [Description("Adds a new to-do task. Returns the created task as JSON.")]
    public async Task<string> AddTaskAsync(
        [Description("Title of the task.")] string title,
        [Description("Due date in ISO format (YYYY-MM-DD).")] string due_date,
        [Description("Recurrence type: none, daily, weekly, monthly, yearly. Defaults to none.")] string recurrence = "none",
        [Description("Optional description of the task.")] string? description = null)
    {
        try
        {
            if (!DateOnly.TryParse(due_date, out var dueDate))
                return "error: invalid due_date format, expected YYYY-MM-DD";

            if (!Enum.TryParse<RecurrenceType>(recurrence, ignoreCase: true, out var recurrenceType))
                return "error: invalid recurrence, expected none/daily/weekly/monthly/yearly";

            var item = new TodoItem(
                Id: Guid.NewGuid(),
                Title: title,
                Description: description,
                DueDate: dueDate,
                Recurrence: recurrenceType,
                CreatedAt: DateTimeOffset.UtcNow);

            var active = await repository.GetActiveAsync();
            active.Add(item);
            await repository.SaveActiveAsync(active);

            return JsonSerializer.Serialize(item, JsonOptions);
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_tasks")]
    [Description("Lists active to-do tasks, optionally filtered by due date range. Returns a JSON array.")]
    public async Task<string> ListTasksAsync(
        [Description("Optional ISO date (YYYY-MM-DD). Only return tasks due before this date.")] string? due_before = null,
        [Description("Optional ISO date (YYYY-MM-DD). Only return tasks due after this date.")] string? due_after = null)
    {
        try
        {
            DateOnly? before = null;
            DateOnly? after = null;

            if (due_before is not null && !DateOnly.TryParse(due_before, out var b))
                return "error: invalid due_before format, expected YYYY-MM-DD";
            else if (due_before is not null)
                before = DateOnly.Parse(due_before);

            if (due_after is not null && !DateOnly.TryParse(due_after, out var a))
                return "error: invalid due_after format, expected YYYY-MM-DD";
            else if (due_after is not null)
                after = DateOnly.Parse(due_after);

            var active = await repository.GetActiveAsync();
            var filtered = active
                .Where(t => before is null || t.DueDate < before.Value)
                .Where(t => after is null || t.DueDate > after.Value)
                .ToList();

            return JsonSerializer.Serialize(filtered, JsonOptions);
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "complete_task")]
    [Description("Marks a task as completed. For repeating tasks, creates the next occurrence from the original due date.")]
    public async Task<string> CompleteTaskAsync(
        [Description("GUID of the task to complete.")] string id)
    {
        try
        {
            if (!Guid.TryParse(id, out var guid))
                return "error: invalid id, expected a GUID";

            var active = await repository.GetActiveAsync();
            var task = active.FirstOrDefault(t => t.Id == guid);
            if (task is null)
                return "error: task not found";

            active.Remove(task);

            var completed = await repository.GetCompletedAsync();
            var completedItem = new CompletedTodoItem(
                Id: task.Id,
                Title: task.Title,
                Description: task.Description,
                DueDate: task.DueDate,
                Recurrence: task.Recurrence,
                CreatedAt: task.CreatedAt,
                CompletedAt: DateTimeOffset.UtcNow);
            completed.Add(completedItem);

            if (task.Recurrence != RecurrenceType.None)
            {
                var nextDue = task.Recurrence switch
                {
                    RecurrenceType.Daily   => task.DueDate.AddDays(1),
                    RecurrenceType.Weekly  => task.DueDate.AddDays(7),
                    RecurrenceType.Monthly => task.DueDate.AddMonths(1),
                    RecurrenceType.Yearly  => task.DueDate.AddYears(1),
                    _ => task.DueDate
                };

                active.Add(task with { Id = Guid.NewGuid(), DueDate = nextDue, CreatedAt = DateTimeOffset.UtcNow });
            }

            await repository.SaveActiveAsync(active);
            await repository.SaveCompletedAsync(completed);

            return JsonSerializer.Serialize(completedItem, JsonOptions);
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "delete_task")]
    [Description("Deletes an active task without marking it as completed.")]
    public async Task<string> DeleteTaskAsync(
        [Description("GUID of the task to delete.")] string id)
    {
        try
        {
            if (!Guid.TryParse(id, out var guid))
                return "error: invalid id, expected a GUID";

            var active = await repository.GetActiveAsync();
            var task = active.FirstOrDefault(t => t.Id == guid);
            if (task is null)
                return "error: task not found";

            active.Remove(task);
            await repository.SaveActiveAsync(active);

            return $"deleted task {guid}";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "update_task")]
    [Description("Updates fields on an active task. Only provided fields are changed.")]
    public async Task<string> UpdateTaskAsync(
        [Description("GUID of the task to update.")] string id,
        [Description("New title.")] string? title = null,
        [Description("New description.")] string? description = null,
        [Description("New due date in ISO format (YYYY-MM-DD).")] string? due_date = null)
    {
        try
        {
            if (!Guid.TryParse(id, out var guid))
                return "error: invalid id, expected a GUID";

            DateOnly? newDue = null;
            if (due_date is not null)
            {
                if (!DateOnly.TryParse(due_date, out var d))
                    return "error: invalid due_date format, expected YYYY-MM-DD";
                newDue = d;
            }

            var active = await repository.GetActiveAsync();
            var index = active.FindIndex(t => t.Id == guid);
            if (index < 0)
                return "error: task not found";

            var existing = active[index];
            var updated = existing with
            {
                Title = title ?? existing.Title,
                Description = description ?? existing.Description,
                DueDate = newDue ?? existing.DueDate
            };
            active[index] = updated;

            await repository.SaveActiveAsync(active);

            return JsonSerializer.Serialize(updated, JsonOptions);
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "list_completed")]
    [Description("Lists completed tasks, optionally filtered by completion date range. Returns a JSON array.")]
    public async Task<string> ListCompletedAsync(
        [Description("Optional ISO datetime. Only return tasks completed after this time.")] string? completed_after = null,
        [Description("Optional ISO datetime. Only return tasks completed before this time.")] string? completed_before = null)
    {
        try
        {
            DateTimeOffset? after = null;
            DateTimeOffset? before = null;

            if (completed_after is not null && !DateTimeOffset.TryParse(completed_after, out var a))
                return "error: invalid completed_after format";
            else if (completed_after is not null)
                after = DateTimeOffset.Parse(completed_after);

            if (completed_before is not null && !DateTimeOffset.TryParse(completed_before, out var b))
                return "error: invalid completed_before format";
            else if (completed_before is not null)
                before = DateTimeOffset.Parse(completed_before);

            var completed = await repository.GetCompletedAsync();
            var filtered = completed
                .Where(t => after is null || t.CompletedAt > after.Value)
                .Where(t => before is null || t.CompletedAt < before.Value)
                .ToList();

            return JsonSerializer.Serialize(filtered, JsonOptions);
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }
}

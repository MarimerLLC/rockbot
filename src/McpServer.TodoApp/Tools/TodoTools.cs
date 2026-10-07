using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpServer.TodoApp.Models;
using McpServer.TodoApp.Services;
using ModelContextProtocol;
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
        [Description("Recurrence type: none, daily, weekly, monthly, quarterly, biannual, yearly. Defaults to none.")] string recurrence = "none",
        [Description("Optional description of the task.")] string? description = null,
        [Description(RecurrenceUntilDescription)] string? recurrence_until = null,
        [Description(RecurrenceCountDescription)] int? recurrence_count = null)
    {
        try
        {
            if (!DateOnly.TryParse(due_date, out var dueDate))
                throw new McpException("invalid due_date format, expected YYYY-MM-DD");

            var recurrenceType = ParseRecurrence(recurrence);
            DateOnly? until = recurrence_until is null ? null : ParseUntil(recurrence_until);
            int? count = recurrence_count is null ? null : ParseCount(recurrence_count.Value);
            RequireRecurringForLimits(recurrenceType, until, count);

            var id = Guid.NewGuid();
            var item = new TodoItem(
                Id: id,
                Title: title,
                Description: description,
                DueDate: dueDate,
                Recurrence: recurrenceType,
                CreatedAt: DateTimeOffset.UtcNow,
                SeriesId: id,
                RecurrenceUntil: until,
                RecurrenceCount: count);

            var active = await repository.GetActiveAsync();
            active.Add(item);
            await repository.SaveActiveAsync(active);

            return JsonSerializer.Serialize(item, JsonOptions);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
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
                throw new McpException("invalid due_before format, expected YYYY-MM-DD");
            else if (due_before is not null)
                before = DateOnly.Parse(due_before);

            if (due_after is not null && !DateOnly.TryParse(due_after, out var a))
                throw new McpException("invalid due_after format, expected YYYY-MM-DD");
            else if (due_after is not null)
                after = DateOnly.Parse(due_after);

            var active = await repository.GetActiveAsync();
            var filtered = active
                .Where(t => before is null || t.DueDate < before.Value)
                .Where(t => after is null || t.DueDate > after.Value)
                .ToList();

            return JsonSerializer.Serialize(filtered, JsonOptions);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "complete_task")]
    [Description(
        "Marks a task as completed. For repeating tasks, creates the next occurrence from the original due date " +
        "unless stop_recurrence is true or the series' recurrence_until / recurrence_count limit is reached. " +
        "Returns JSON { completed, next, seriesEnded }: next is the newly created occurrence (with its new id) or null; " +
        "seriesEnded is stop_recurrence, count_reached or until_reached when a repeating series ended, otherwise null.")]
    public async Task<string> CompleteTaskAsync(
        [Description("GUID of the task to complete.")] string id,
        [Description("If true, complete this occurrence and end the series: no next occurrence is created. Defaults to false.")] bool stop_recurrence = false)
    {
        try
        {
            if (!Guid.TryParse(id, out var guid))
                throw new McpException("invalid id, expected a GUID");

            var active = await repository.GetActiveAsync();
            var task = active.FirstOrDefault(t => t.Id == guid);
            if (task is null)
                throw new McpException("task not found");

            active.Remove(task);

            var completed = await repository.GetCompletedAsync();
            var completedItem = new CompletedTodoItem(
                Id: task.Id,
                Title: task.Title,
                Description: task.Description,
                DueDate: task.DueDate,
                Recurrence: task.Recurrence,
                CreatedAt: task.CreatedAt,
                CompletedAt: DateTimeOffset.UtcNow,
                SeriesId: task.SeriesId,
                Occurrence: task.Occurrence);
            completed.Add(completedItem);

            TodoItem? next = null;
            string? seriesEnded = null;
            if (task.Recurrence != RecurrenceType.None)
            {
                var nextDue = NextDueDate(task);
                if (stop_recurrence)
                    seriesEnded = "stop_recurrence";
                else if (task.RecurrenceCount is { } count && task.Occurrence + 1 > count)
                    seriesEnded = "count_reached";
                else if (task.RecurrenceUntil is { } until && nextDue > until)
                    seriesEnded = "until_reached";
                else
                {
                    next = task with
                    {
                        Id = Guid.NewGuid(),
                        DueDate = nextDue,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Occurrence = task.Occurrence + 1
                    };
                    active.Add(next);
                }
            }

            await repository.SaveActiveAsync(active);
            await repository.SaveCompletedAsync(completed);

            return JsonSerializer.Serialize(new { completed = completedItem, next, seriesEnded }, JsonOptions);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
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
                throw new McpException("invalid id, expected a GUID");

            var active = await repository.GetActiveAsync();
            var task = active.FirstOrDefault(t => t.Id == guid);
            if (task is null)
                throw new McpException("task not found");

            active.Remove(task);
            await repository.SaveActiveAsync(active);

            return $"deleted task {guid}";
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "update_task")]
    [Description("Updates fields on an active task. Only provided fields are changed.")]
    public async Task<string> UpdateTaskAsync(
        [Description("GUID of the task to update.")] string id,
        [Description("New title.")] string? title = null,
        [Description("New description.")] string? description = null,
        [Description("New due date in ISO format (YYYY-MM-DD).")] string? due_date = null,
        [Description("New recurrence type: none, daily, weekly, monthly, quarterly, biannual, yearly. " +
                     "Setting none ends the series but keeps this task and its id; it also clears any series limits.")] string? recurrence = null,
        [Description(RecurrenceUntilDescription + " Pass an empty string to remove an existing end date.")] string? recurrence_until = null,
        [Description(RecurrenceCountDescription + " Pass 0 to remove an existing count limit.")] int? recurrence_count = null)
    {
        try
        {
            if (!Guid.TryParse(id, out var guid))
                throw new McpException("invalid id, expected a GUID");

            DateOnly? newDue = null;
            if (due_date is not null)
            {
                if (!DateOnly.TryParse(due_date, out var d))
                    throw new McpException("invalid due_date format, expected YYYY-MM-DD");
                newDue = d;
            }

            RecurrenceType? newRecurrence = recurrence is null ? null : ParseRecurrence(recurrence);
            if (recurrence_count < 0)
                throw new McpException("invalid recurrence_count, expected 0 (to clear) or a positive integer");

            var active = await repository.GetActiveAsync();
            var index = active.FindIndex(t => t.Id == guid);
            if (index < 0)
                throw new McpException("task not found");

            var existing = active[index];
            var resultingRecurrence = newRecurrence ?? existing.Recurrence;

            var until = recurrence_until switch
            {
                null => existing.RecurrenceUntil,
                "" => null,
                _ => ParseUntil(recurrence_until)
            };
            var count = recurrence_count switch
            {
                null => existing.RecurrenceCount,
                0 => null,
                _ => recurrence_count
            };

            if (resultingRecurrence == RecurrenceType.None)
            {
                // Explicit limits on a non-repeating task would be silently meaningless, so reject them;
                // limits left over from the series being ended are simply dropped.
                RequireRecurringForLimits(
                    resultingRecurrence,
                    string.IsNullOrEmpty(recurrence_until) ? null : until,
                    recurrence_count is null or 0 ? null : count);
                until = null;
                count = null;
            }

            var updated = existing with
            {
                Title = title ?? existing.Title,
                Description = description ?? existing.Description,
                DueDate = newDue ?? existing.DueDate,
                Recurrence = resultingRecurrence,
                RecurrenceUntil = until,
                RecurrenceCount = count
            };
            active[index] = updated;

            await repository.SaveActiveAsync(active);

            return JsonSerializer.Serialize(updated, JsonOptions);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
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
                throw new McpException("invalid completed_after format");
            else if (completed_after is not null)
                after = DateTimeOffset.Parse(completed_after);

            if (completed_before is not null && !DateTimeOffset.TryParse(completed_before, out var b))
                throw new McpException("invalid completed_before format");
            else if (completed_before is not null)
                before = DateTimeOffset.Parse(completed_before);

            var completed = await repository.GetCompletedAsync();
            var filtered = completed
                .Where(t => after is null || t.CompletedAt > after.Value)
                .Where(t => before is null || t.CompletedAt < before.Value)
                .ToList();

            return JsonSerializer.Serialize(filtered, JsonOptions);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    private const string RecurrenceUntilDescription =
        "Optional ISO date (YYYY-MM-DD) ending a repeating series. Inclusive: an occurrence due on this date is still created, " +
        "none after it. Only valid when recurrence is not none.";

    private const string RecurrenceCountDescription =
        "Optional total number of occurrences in a repeating series, counting the first. Completing the last one ends the series. " +
        "Only valid when recurrence is not none. For series created before series tracking existed, counting starts at the current occurrence.";

    private static RecurrenceType ParseRecurrence(string recurrence) =>
        Enum.TryParse<RecurrenceType>(recurrence, ignoreCase: true, out var value)
            ? value
            : throw new McpException("invalid recurrence, expected none/daily/weekly/monthly/quarterly/biannual/yearly");

    private static DateOnly ParseUntil(string recurrenceUntil) =>
        DateOnly.TryParse(recurrenceUntil, out var value)
            ? value
            : throw new McpException("invalid recurrence_until format, expected YYYY-MM-DD");

    private static int ParseCount(int recurrenceCount) =>
        recurrenceCount >= 1
            ? recurrenceCount
            : throw new McpException("invalid recurrence_count, expected a positive integer");

    private static void RequireRecurringForLimits(RecurrenceType recurrence, DateOnly? until, int? count)
    {
        if (recurrence == RecurrenceType.None && (until is not null || count is not null))
            throw new McpException("recurrence_until and recurrence_count require a recurrence other than none");
    }

    private static DateOnly NextDueDate(TodoItem task) => task.Recurrence switch
    {
        RecurrenceType.Daily     => task.DueDate.AddDays(1),
        RecurrenceType.Weekly    => task.DueDate.AddDays(7),
        RecurrenceType.Monthly   => task.DueDate.AddMonths(1),
        RecurrenceType.Quarterly => task.DueDate.AddMonths(3),
        RecurrenceType.BiAnnual  => task.DueDate.AddMonths(6),
        RecurrenceType.Yearly    => task.DueDate.AddYears(1),
        _ => task.DueDate
    };
}

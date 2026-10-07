using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using McpServer.TodoApp.Models;
using McpServer.TodoApp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpServer.TodoApp.Tools;

[McpServerToolType]
public sealed class TodoTools(TodoRepository repository)
{
    [McpServerTool(Name = "add_task", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a new to-do task. Returns the created task as JSON.")]
    public async Task<string> AddTaskAsync(
        [Description("Title of the task.")] string title,
        [Description("Due date in ISO format (YYYY-MM-DD).")] string due_date,
        [Description("Recurrence type. Defaults to none.")] RecurrenceType recurrence = RecurrenceType.None,
        [Description("Optional description of the task's intent. Record later status updates with add_task_note, not here.")] string? description = null,
        [Description(RecurrenceUntilDescription)] string? recurrence_until = null,
        [Description(RecurrenceCountDescription)] int? recurrence_count = null,
        [Description(MonthAnchorDescription + " With last_day, due_date is moved to the last day of its month.")] MonthAnchor? month_anchor = null)
    {
        try
        {
            if (!DateOnly.TryParse(due_date, out var dueDate))
                throw new McpException("invalid due_date format, expected YYYY-MM-DD");

            var recurrenceType = recurrence;
            DateOnly? until = recurrence_until is null ? null : ParseUntil(recurrence_until);
            int? count = recurrence_count is null ? null : ParseCount(recurrence_count.Value);
            RequireRecurringForLimits(recurrenceType, until, count);
            var (anchor, anchorDay, anchoredDue) = ResolveAnchor(
                recurrenceType, month_anchor, existing: null, dueDate, dueDateExplicit: false);

            var id = Guid.NewGuid();
            var item = new TodoItem(
                Id: id,
                Title: title,
                Description: description,
                DueDate: anchoredDue,
                Recurrence: recurrenceType,
                CreatedAt: DateTimeOffset.UtcNow,
                SeriesId: id,
                RecurrenceUntil: until,
                RecurrenceCount: count,
                MonthAnchor: anchor,
                AnchorDay: anchorDay);

            var active = await repository.GetActiveAsync();
            active.Add(item);
            await repository.SaveActiveAsync(active);

            return JsonSerializer.Serialize(item, ToolJson.Options);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "list_tasks", ReadOnly = true, OpenWorld = false)]
    [Description("Lists active to-do tasks, optionally filtered by due date range. Returns a JSON array. " +
                 "Notes are omitted; each task shows note_count and its most recent note as last_note.")]
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

            return JsonSerializer.Serialize(filtered.Select(ToListView), ToolJson.Options);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "add_task_note", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Appends a timestamped note to an active task's activity log without changing its description. Use this for status " +
        "updates, verification results and other agent observations instead of editing description. Returns the task with all its notes.")]
    public async Task<string> AddTaskNoteAsync(
        [Description("GUID of the task.")] string id,
        [Description("The note text.")] string text,
        [Description("Optional short label for who or what wrote the note, e.g. heartbeat-patrol.")] string? source = null)
    {
        try
        {
            if (!Guid.TryParse(id, out var guid))
                throw new McpException("invalid id, expected a GUID");
            if (string.IsNullOrWhiteSpace(text))
                throw new McpException("text must not be empty");

            var active = await repository.GetActiveAsync();
            var index = active.FindIndex(t => t.Id == guid);
            if (index < 0)
                throw new McpException("task not found");

            var existing = active[index];
            var note = new TaskNote(DateTimeOffset.UtcNow, text.Trim(), string.IsNullOrWhiteSpace(source) ? null : source.Trim());
            var updated = existing with { Notes = [.. existing.Notes ?? [], note] };
            active[index] = updated;
            await repository.SaveActiveAsync(active);

            return JsonSerializer.Serialize(updated, ToolJson.Options);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "complete_task", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Marks a task as completed. For repeating tasks, creates the next occurrence from the original due date " +
        "(monthly-type series land on their anchor day, or on the last day of the month for month_anchor last_day) " +
        "unless stop_recurrence is true or the series' recurrence_until / recurrence_count limit is reached. " +
        "Returns JSON { completed, next, series_ended }: next is the newly created occurrence (with its new id) or null; " +
        "series_ended is stop_recurrence, count_reached or until_reached when a repeating series ended, otherwise null.")]
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
                Occurrence: task.Occurrence,
                Notes: task.Notes);
            completed.Add(completedItem);

            TodoItem? next = null;
            string? seriesEnded = null;
            if (task.Recurrence != RecurrenceType.None)
            {
                var nextDue = Recurrence.NextDueDate(task);
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
                        Occurrence = task.Occurrence + 1,
                        Notes = null
                    };
                    active.Add(next);
                }
            }

            await repository.SaveActiveAsync(active);
            await repository.SaveCompletedAsync(completed);

            return JsonSerializer.Serialize(new { completed = completedItem, next, seriesEnded }, ToolJson.Options);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "delete_task", Destructive = true, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = "update_task", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Updates fields on an active task. Only provided fields are changed.")]
    public async Task<string> UpdateTaskAsync(
        [Description("GUID of the task to update.")] string id,
        [Description("New title.")] string? title = null,
        [Description("New description (replaces the old one). For status updates or observations, use add_task_note instead.")] string? description = null,
        [Description("New due date in ISO format (YYYY-MM-DD).")] string? due_date = null,
        [Description("New recurrence type. " +
                     "Setting none ends the series but keeps this task and its id; it also clears any series limits.")] RecurrenceType? recurrence = null,
        [Description(RecurrenceUntilDescription + " Pass an empty string to remove an existing end date.")] string? recurrence_until = null,
        [Description(RecurrenceCountDescription + " Pass 0 to remove an existing count limit.")] int? recurrence_count = null,
        [Description(MonthAnchorDescription + " Switching to last_day moves the current due date to the last day of its month " +
                     "unless due_date is also given.")] MonthAnchor? month_anchor = null)
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

            var newRecurrence = recurrence;
            var newAnchor = month_anchor;
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

            var (anchor, anchorDay, anchoredDue) = ResolveAnchor(
                resultingRecurrence, newAnchor, existing, newDue ?? existing.DueDate, dueDateExplicit: newDue is not null);

            var updated = existing with
            {
                Title = title ?? existing.Title,
                Description = description ?? existing.Description,
                DueDate = anchoredDue,
                Recurrence = resultingRecurrence,
                RecurrenceUntil = until,
                RecurrenceCount = count,
                MonthAnchor = anchor,
                AnchorDay = anchorDay
            };
            active[index] = updated;

            await repository.SaveActiveAsync(active);

            return JsonSerializer.Serialize(updated, ToolJson.Options);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "list_completed", ReadOnly = true, OpenWorld = false)]
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

            return JsonSerializer.Serialize(filtered, ToolJson.Options);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    private const string RecurrenceUntilDescription =
        "Optional ISO date (YYYY-MM-DD) ending a repeating series. Inclusive: an occurrence due on this date is still created, " +
        "none after it. Only valid when recurrence is not none.";

    private const string MonthAnchorDescription =
        "Optional day-of-month rule for monthly, quarterly, biannual and yearly series: same_day (default) repeats on the " +
        "original day of the month, using the month's last day when it is shorter (a 31st lands on Feb 28, then back on Mar 31); " +
        "last_day repeats on the last day of every month.";

    private const string RecurrenceCountDescription =
        "Optional total number of occurrences in a repeating series, counting the first. Completing the last one ends the series. " +
        "Only valid when recurrence is not none. For series created before series tracking existed, counting starts at the current occurrence.";

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

    /// <summary>
    /// Works out the day-of-month anchor (and possibly adjusted due date) for a task's resulting recurrence.
    /// Month-based series keep their original anchor day across clamped months; switching to last_day snaps
    /// the due date to month-end unless the caller supplied the due date explicitly.
    /// </summary>
    private static (MonthAnchor? Anchor, int? AnchorDay, DateOnly DueDate) ResolveAnchor(
        RecurrenceType recurrence, MonthAnchor? requested, TodoItem? existing, DateOnly dueDate, bool dueDateExplicit)
    {
        if (!Recurrence.IsMonthBased(recurrence))
        {
            if (requested is not null)
                throw new McpException("month_anchor requires a monthly, quarterly, biannual or yearly recurrence");
            return (null, null, dueDate);
        }

        var anchor = requested ?? existing?.MonthAnchor ?? MonthAnchor.SameDay;
        if (anchor == MonthAnchor.LastDay)
            return (anchor, null, dueDateExplicit ? dueDate : Recurrence.EndOfMonth(dueDate));

        // Keep an established anchor day (e.g. 31 while the current occurrence is clamped to Feb 28)
        // unless the caller moved the due date or the task wasn't anchored on a day before.
        var keepDay = !dueDateExplicit && existing?.MonthAnchor == MonthAnchor.SameDay && existing.AnchorDay is not null;
        return (anchor, keepDay ? existing!.AnchorDay : dueDate.Day, dueDate);
    }

    /// <summary>List form of a task: the notes log is replaced by its count and latest entry to keep listings small.</summary>
    private static JsonNode ToListView(TodoItem task)
    {
        var node = JsonSerializer.SerializeToNode(task with { Notes = null }, ToolJson.Options)!.AsObject();
        node.Remove("notes");
        node["note_count"] = task.Notes?.Count ?? 0;
        node["last_note"] = task.Notes is { Count: > 0 } notes
            ? JsonSerializer.SerializeToNode(notes[^1], ToolJson.Options)
            : null;
        return node;
    }
}

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
    [Description("Lists active to-do tasks, sorted by due date by default, optionally filtered by due date range or text. " +
                 "Returns a JSON array. Notes are omitted; each task shows note_count and its most recent note as last_note. " +
                 "Use compact for an overview and get_task for one task's full detail.")]
    public async Task<string> ListTasksAsync(
        [Description("Optional ISO date (YYYY-MM-DD). Only return tasks due before this date.")] string? due_before = null,
        [Description("Optional ISO date (YYYY-MM-DD). Only return tasks due after this date.")] string? due_after = null,
        [Description(QueryDescription)] string? query = null,
        [Description("Sort order: due_date (default; ties by title), created_at, or title.")] TaskSort sort = TaskSort.DueDate,
        [Description(CompactDescription)] bool compact = false)
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
                .Where(t => Matches(query, t.Title, t.Description));

            var sorted = sort switch
            {
                TaskSort.CreatedAt => filtered.OrderBy(t => t.CreatedAt),
                TaskSort.Title => filtered.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.DueDate),
                _ => filtered.OrderBy(t => t.DueDate).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            };

            return JsonSerializer.Serialize(
                sorted.Select(t => compact ? Compact(t.Id, t.Title, t.DueDate, t.Recurrence) : ToListView(t)),
                ToolJson.Options);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "get_task", ReadOnly = true, OpenWorld = false)]
    [Description("Gets one task by id with its full detail, including all notes. Looks in active tasks first, then " +
                 "completed history. Returns JSON { status, task } where status is active or completed.")]
    public async Task<string> GetTaskAsync(
        [Description("GUID of the task.")] string id)
    {
        try
        {
            if (!Guid.TryParse(id, out var guid))
                throw new McpException("invalid id, expected a GUID");

            if ((await repository.GetActiveAsync()).FirstOrDefault(t => t.Id == guid) is { } active)
                return JsonSerializer.Serialize(new { status = "active", task = active }, ToolJson.Options);

            if ((await repository.GetCompletedAsync()).FirstOrDefault(t => t.Id == guid) is { } completed)
                return JsonSerializer.Serialize(new { status = "completed", task = completed }, ToolJson.Options);

            if ((await repository.GetDeletedAsync()).FirstOrDefault(d => d.Task.Id == guid) is { } deleted)
            {
                return JsonSerializer.Serialize(
                    new { status = "deleted", task = deleted.Task, deletedAt = deleted.DeletedAt, purgeAfter = PurgeAfter(deleted) },
                    ToolJson.Options);
            }

            throw new McpException("task not found");
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
                Notes: task.Notes,
                RecurrenceUntil: task.RecurrenceUntil,
                RecurrenceCount: task.RecurrenceCount,
                MonthAnchor: task.MonthAnchor,
                AnchorDay: task.AnchorDay);
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
    [Description("Deletes an active task without marking it as completed. The delete is recoverable: the task keeps its id " +
                 "and can be brought back with restore_task until it is purged (see purge_after in the result).")]
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

            var deleted = await repository.GetDeletedAsync();
            var entry = new DeletedTodoItem(task, DateTimeOffset.UtcNow);
            deleted.Add(entry);
            await repository.SaveDeletedAsync(deleted);
            active.Remove(task);
            await repository.SaveActiveAsync(active);

            return JsonSerializer.Serialize(DeletedView(entry), ToolJson.Options);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "restore_task", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Restores a deleted task to the active list with its original id, notes and series. Returns the restored task.")]
    public async Task<string> RestoreTaskAsync(
        [Description("GUID of the deleted task.")] string id)
    {
        try
        {
            if (!Guid.TryParse(id, out var guid))
                throw new McpException("invalid id, expected a GUID");

            var deleted = await repository.GetDeletedAsync();
            var entry = deleted.FirstOrDefault(d => d.Task.Id == guid)
                ?? throw new McpException("deleted task not found (it may have been purged, or was never deleted)");

            var active = await repository.GetActiveAsync();
            active.Add(entry.Task);
            await repository.SaveActiveAsync(active);
            deleted.Remove(entry);
            await repository.SaveDeletedAsync(deleted);

            return JsonSerializer.Serialize(entry.Task, ToolJson.Options);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "list_deleted", ReadOnly = true, OpenWorld = false)]
    [Description("Lists deleted tasks that can still be restored, most recently deleted first. Each entry has the task's " +
                 "id, title, due_date and recurrence plus deleted_at and purge_after.")]
    public async Task<string> ListDeletedAsync()
    {
        try
        {
            var deleted = await repository.GetDeletedAsync();
            return JsonSerializer.Serialize(
                deleted.OrderByDescending(d => d.DeletedAt).Select(DeletedView), ToolJson.Options);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    [McpServerTool(Name = "uncomplete_task", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Reverses complete_task: moves a completed task back to the active list with its original id. If completing it " +
        "created a next occurrence, that occurrence is removed again, but only while it is untouched. If the next " +
        "occurrence was edited, noted or completed since, the call fails so nothing is lost. Returns JSON " +
        "{ restored, removed_next } where removed_next is the id of the removed occurrence or null.")]
    public async Task<string> UncompleteTaskAsync(
        [Description("GUID of the completed task (the id it had when it was completed).")] string id)
    {
        try
        {
            if (!Guid.TryParse(id, out var guid))
                throw new McpException("invalid id, expected a GUID");

            var completed = await repository.GetCompletedAsync();
            var done = completed.FirstOrDefault(t => t.Id == guid)
                ?? throw new McpException("completed task not found");

            var restored = new TodoItem(
                Id: done.Id,
                Title: done.Title,
                Description: done.Description,
                DueDate: done.DueDate,
                Recurrence: done.Recurrence,
                CreatedAt: done.CreatedAt,
                SeriesId: done.SeriesId,
                RecurrenceUntil: done.RecurrenceUntil,
                RecurrenceCount: done.RecurrenceCount,
                Occurrence: done.Occurrence,
                MonthAnchor: done.MonthAnchor,
                AnchorDay: done.AnchorDay,
                Notes: done.Notes);

            var active = await repository.GetActiveAsync();
            Guid? removedNext = null;
            if (done.Recurrence != RecurrenceType.None)
            {
                // Completions recorded before the completed record kept series limits and the month anchor lack them;
                // the spawned occurrence inherited them, so take them from there.
                var linked = active.FirstOrDefault(t => t.Id != done.Id && t.SeriesId == done.SeriesId && t.Occurrence == done.Occurrence + 1);
                var hasSeriesFields = done.MonthAnchor is not null || done.RecurrenceUntil is not null || done.RecurrenceCount is not null;
                if (!hasSeriesFields && linked is not null)
                {
                    restored = restored with
                    {
                        RecurrenceUntil = linked.RecurrenceUntil,
                        RecurrenceCount = linked.RecurrenceCount,
                        MonthAnchor = linked.MonthAnchor,
                        AnchorDay = linked.AnchorDay
                    };
                }

                var expected = restored with
                {
                    DueDate = Recurrence.NextDueDate(restored),
                    Occurrence = restored.Occurrence + 1,
                    Notes = null
                };

                if (FindSpawnedNext(completed, expected, done) is { } nextCompleted)
                    throw new McpException(
                        $"cannot uncomplete: its next occurrence {nextCompleted.Id} was already completed; uncomplete that one first");

                if (FindSpawnedNext(active, expected, done) is { } next)
                {
                    if (!IsUntouched(next, expected))
                        throw new McpException(
                            $"cannot uncomplete: its next occurrence {next.Id} has been edited or has notes since it was created; " +
                            "delete or adjust that occurrence first if you still want to undo this completion");
                    active.Remove(next);
                    removedNext = next.Id;
                }
            }

            active.Add(restored);
            await repository.SaveActiveAsync(active);
            completed.Remove(done);
            await repository.SaveCompletedAsync(completed);

            return JsonSerializer.Serialize(new { restored, removedNext }, ToolJson.Options);
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
    [Description("Lists completed tasks, most recently completed first by default, optionally filtered by completion date " +
                 "range or text. Returns a JSON array.")]
    public async Task<string> ListCompletedAsync(
        [Description("Optional ISO datetime. Only return tasks completed after this time.")] string? completed_after = null,
        [Description("Optional ISO datetime. Only return tasks completed before this time.")] string? completed_before = null,
        [Description(QueryDescription)] string? query = null,
        [Description("Sort order: completed_at (default; most recent first), due_date, or title.")] CompletedSort sort = CompletedSort.CompletedAt,
        [Description(CompactDescription)] bool compact = false)
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
                .Where(t => Matches(query, t.Title, t.Description));

            var sorted = sort switch
            {
                CompletedSort.DueDate => filtered.OrderBy(t => t.DueDate).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
                CompletedSort.Title => filtered.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ThenByDescending(t => t.CompletedAt),
                _ => filtered.OrderByDescending(t => t.CompletedAt)
            };

            return compact
                ? JsonSerializer.Serialize(
                    sorted.Select(t => Compact(t.Id, t.Title, t.DueDate, t.Recurrence, t.CompletedAt)), ToolJson.Options)
                : JsonSerializer.Serialize(sorted, ToolJson.Options);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    private const string RecurrenceUntilDescription =
        "Optional ISO date (YYYY-MM-DD) ending a repeating series. Inclusive: an occurrence due on this date is still created, " +
        "none after it. Only valid when recurrence is not none.";

    private const string QueryDescription =
        "Optional text to search for: a case-insensitive substring match against title and description.";

    private const string CompactDescription =
        "If true, return only id, title, due_date and recurrence (plus completed_at for completed tasks) for each task. Defaults to false.";

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

    private DateTimeOffset PurgeAfter(DeletedTodoItem entry) => entry.DeletedAt.AddDays(repository.RetentionDays);

    private JsonObject DeletedView(DeletedTodoItem entry)
    {
        var node = Compact(entry.Task.Id, entry.Task.Title, entry.Task.DueDate, entry.Task.Recurrence);
        node["deleted_at"] = JsonSerializer.SerializeToNode(entry.DeletedAt, ToolJson.Options);
        node["purge_after"] = JsonSerializer.SerializeToNode(PurgeAfter(entry), ToolJson.Options);
        return node;
    }

    /// <summary>
    /// Finds the occurrence complete_task spawned after <paramref name="done"/>: linked by series and occurrence
    /// number, or, for occurrences created before series tracking existed, by title, recurrence and due date.
    /// </summary>
    private static T? FindSpawnedNext<T>(IEnumerable<T> items, TodoItem expected, CompletedTodoItem done) where T : class
    {
        static (Guid Id, Guid? Series, int Occurrence, string Title, RecurrenceType Recurrence, DateOnly Due) Key(T item) => item switch
        {
            TodoItem t => (t.Id, t.SeriesId, t.Occurrence, t.Title, t.Recurrence, t.DueDate),
            CompletedTodoItem c => (c.Id, c.SeriesId, c.Occurrence, c.Title, c.Recurrence, c.DueDate),
            _ => throw new ArgumentException(nameof(item))
        };

        return items.FirstOrDefault(i =>
            {
                var k = Key(i);
                return k.Id != done.Id && k.Series == done.SeriesId && k.Occurrence == expected.Occurrence;
            })
            ?? items.FirstOrDefault(i =>
            {
                var k = Key(i);
                return k.Id != done.Id && k.Title == expected.Title && k.Recurrence == expected.Recurrence && k.Due == expected.DueDate;
            });
    }

    /// <summary>True when the spawned occurrence still looks exactly as complete_task created it.</summary>
    private static bool IsUntouched(TodoItem next, TodoItem expected) =>
        next.Title == expected.Title
        && next.Description == expected.Description
        && next.DueDate == expected.DueDate
        && next.Recurrence == expected.Recurrence
        && next.RecurrenceUntil == expected.RecurrenceUntil
        && next.RecurrenceCount == expected.RecurrenceCount
        && next.MonthAnchor == expected.MonthAnchor
        && next.Notes is null or { Count: 0 };

    private static bool Matches(string? query, string title, string? description) =>
        string.IsNullOrWhiteSpace(query)
        || title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || (description?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    private static JsonObject Compact(Guid id, string title, DateOnly dueDate, RecurrenceType recurrence, DateTimeOffset? completedAt = null)
    {
        var node = new JsonObject
        {
            ["id"] = id.ToString(),
            ["title"] = title,
            ["due_date"] = dueDate.ToString("yyyy-MM-dd"),
            ["recurrence"] = JsonSerializer.SerializeToNode(recurrence, ToolJson.Options)
        };
        if (completedAt is not null)
            node["completed_at"] = JsonSerializer.SerializeToNode(completedAt, ToolJson.Options);
        return node;
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

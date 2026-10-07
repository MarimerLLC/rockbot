# Todo MCP Server — Skill Document

## Overview

A persistent to-do list with recurring tasks, an activity log per task, and undo for deletes and completions. Every call is strict and every failure is a real tool error, so a call that "succeeds" really did what it says.

> **Source of truth:** this document lives in the RockBot repo at `src/McpServer.TodoApp/SKILL.md`. Edit it there and re-publish it with `update_skill`, so the repo and the aggregator stay in sync.

## Conventions (read these first)

- **Dates are `YYYY-MM-DD`**, date-only, with no time zone. `list_completed` filters (`completed_after` / `completed_before`) take ISO datetimes.
- **Everything is snake_case**, in and out: `due_date`, `series_id`, `month_anchor`, and so on. Enum values are lowercase: `biannual`, not `biAnnual`.
- **Unknown arguments are rejected.** A misspelled or unsupported key fails with an error naming it and listing the valid ones; it is never silently ignored. An enum value outside its allowed list (e.g. `recurrence: "fortnightly"`) fails the same way.
- **Errors are `isError: true` results.** If a call didn't error, it did what it says.
- **Ids are GUIDs.** Every occurrence of a recurring task has its own id, and occurrences share a `series_id`.
- `due_before` and `due_after` are **exclusive**. To get tasks due on or before 2026-10-31, pass `due_before: "2026-11-01"`.

## Tools

| Tool | Use it to |
|------|-----------|
| `list_tasks` | Overview of active tasks, sorted by due date. Use `compact: true` for id/title/due_date/recurrence only, and `query` to search title and description. Notes appear only as `note_count` + `last_note`. |
| `get_task` | Full detail for one id, including all notes. Returns `{ status, task }`; status is `active`, `completed` or `deleted`. |
| `add_task` | Create a task. Optional `recurrence`, `recurrence_until`, `recurrence_count`, `month_anchor`, `description`. |
| `update_task` | Change fields on an active task. The id is always preserved. Can change `recurrence`, the series limits and `month_anchor`. |
| `add_task_note` | Append a status or observation to a task's log. Use this, not `description`, for progress updates. |
| `complete_task` | Complete a task. Returns `{ completed, next, series_ended }`. Pass `stop_recurrence: true` to end a series. |
| `uncomplete_task` | Undo a completion. Removes the auto-created next occurrence if it is untouched. |
| `delete_task` | Soft-delete a task (recoverable for 30 days). |
| `restore_task` / `list_deleted` | Bring back a deleted task / see what can be restored. |
| `list_completed` | Completed history, most recent first. Supports `query`, `compact` and `sort`. |

## Recurrence

- **Values:** `none`, `daily`, `weekly`, `monthly`, `quarterly`, `biannual`, `yearly`.
- **The next occurrence is computed from the series' anchor, not the completion date.** Completing a monthly task late doesn't shift the schedule.
- **`month_anchor`** applies to monthly, quarterly, biannual and yearly series:
  - `same_day` (default) keeps the original day of the month. A short month uses its last day, then the series returns to the anchor: Jan 31 → Feb 28 → Mar 31.
  - `last_day` lands on the last day of every month. Use it for "month-end" tasks.
  - Setting `last_day` moves the current due date to the end of its month, unless you also pass `due_date`.
  - Using `month_anchor` with `none`, `daily` or `weekly` is an error.
- **Ending a series:**
  - `complete_task(id, stop_recurrence: true)` completes this occurrence and creates no more.
  - `update_task(id, recurrence: "none")` keeps the task (and its id) but stops it from repeating.
  - `recurrence_until` (inclusive date) and `recurrence_count` (total occurrences, counting the first) set limits up front.
  - In `update_task`, `recurrence_until: ""` or `recurrence_count: 0` removes a limit.
  - Setting a limit on a non-recurring task is an error.
- **Always read `complete_task`'s result.** `next` is the new occurrence, with its new id, or `null`. When a series ended, `series_ended` says why: `stop_recurrence`, `count_reached` or `until_reached`. If you expected a series to stop and `next` isn't null, fix it right away. Either:
  - `uncomplete_task`, then `complete_task` with `stop_recurrence: true`; or
  - `delete_task` on the `next` id.

## Notes vs. description

- **`description` is the task's intent.** Don't append status lines ("re-verified on …", "prep refreshed at …") to it.
- **Use `add_task_note(id, text, source?)`** for status, verification results and handoff pointers. It records a server timestamp. Set `source` to your agent or job name, e.g. `heartbeat-patrol`.
- **Notes stay with their occurrence.** When a recurring task is completed, its notes go into completed history, and the next occurrence starts with an empty log.

## Undo (act on corrections without extra confirmation)

- **`delete_task`** is recoverable. `restore_task(id)` brings the task back unchanged until its `purge_after` time. Use `list_deleted` to find ids.
- **`uncomplete_task(id)`** reverses a completion. It refuses, changing nothing, if the auto-created next occurrence was edited, noted or completed since. The error names that occurrence, so you can decide what to do with it.

## Common workflows

**Find a task without listing everything**
```json
list_tasks({ "query": "invoice", "compact": true })
get_task({ "id": "<id from the list>" })
```

**Create a month-end recurring task**
```json
add_task({ "title": "PWOP invoice", "due_date": "2026-10-31", "recurrence": "monthly", "month_anchor": "last_day" })
```

**Record progress on a task**
```json
add_task_note({ "id": "<id>", "text": "Prep doc refreshed: drafts/x.md", "source": "heartbeat-patrol" })
```

**Finish a recurring task for good**
```json
complete_task({ "id": "<id>", "stop_recurrence": true })
```
Check that the result has `next: null` and `series_ended: "stop_recurrence"`.

**A task should have been due on the 31st but shows the 30th**
```json
update_task({ "id": "<id>", "month_anchor": "last_day" })
```

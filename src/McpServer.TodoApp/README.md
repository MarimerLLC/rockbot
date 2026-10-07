# McpServer.TodoApp

An MCP server providing a persistent to-do list accessible to AI agents via the Model Context Protocol. Runs as an ASP.NET Core 10 web app with JSON file storage at `/data`.

## Tools

| Tool | Parameters | Description |
|------|-----------|-------------|
| `add_task` | `title`, `due_date` (YYYY-MM-DD), optional `recurrence` (none/daily/weekly/monthly/quarterly/biannual/yearly), `description`, `recurrence_until`, `recurrence_count`, `month_anchor` | Adds a new task; returns created task as JSON |
| `list_tasks` | optional `due_before`, `due_after` (YYYY-MM-DD) | Lists active tasks, optionally filtered by due date; notes are summarized as `note_count` + `last_note` |
| `add_task_note` | `id`, `text`, optional `source` | Appends a timestamped note to a task's activity log; returns the task with all notes |
| `complete_task` | `id` (GUID), optional `stop_recurrence` | Marks task complete; returns `{ completed, next, series_ended }` |
| `delete_task` | `id` (GUID) | Removes task from active list |
| `update_task` | `id`, optional `title`, `description`, `due_date`, `recurrence`, `recurrence_until`, `recurrence_count`, `month_anchor` | Updates fields on an active task; the id is preserved |
| `list_completed` | optional `completed_after`, `completed_before` (ISO datetime) | Lists completed tasks |

### Recurrence

- Next-due is calculated from the original due date, not the completion date.
- Monthly, quarterly, biannual and yearly series have a `month_anchor`:
  - `same_day` (default) repeats on the series' original day of the month (`anchor_day`). When a month is shorter, the occurrence lands on that month's last day, and the series returns to its anchor afterwards: Jan 31 → Feb 28 → Mar 31.
  - `last_day` repeats on the last day of every month.
  - Using `month_anchor` with none, daily or weekly is an error.
  - `add_task(month_anchor: "last_day")` moves `due_date` to the end of its month.
  - `update_task(month_anchor: "last_day")` does the same, unless `due_date` is also given.
  - Tasks stored before anchors existed use their current due day as the anchor.
- Every occurrence of a series shares a `series_id` and carries its 1-based `occurrence` number. Tasks stored before series tracking existed get `series_id = id` and start counting at 1.
- A series ends when:
  - `complete_task` is called with `stop_recurrence: true`;
  - the next occurrence would exceed `recurrence_count` (total occurrences, counting the first);
  - the next due date would fall after `recurrence_until` (inclusive).
- `complete_task` returns `next` (the new occurrence with its id, or `null`) and `series_ended` (`stop_recurrence` / `count_reached` / `until_reached`, or `null`).
- `update_task(recurrence: "none")` ends a series without deleting the task and clears its limits. `recurrence_until: ""` and `recurrence_count: 0` clear a single limit.
- Setting a limit on a task whose recurrence is `none` is an error.

### Notes

`description` holds the task's intent. Status updates, verification results and other agent observations go in the append-only notes log via `add_task_note`, not into `description`.
- Each note records `at` (a server timestamp), `text`, and an optional `source` (e.g. `heartbeat-patrol`).
- `list_tasks` omits the log, showing only `note_count` and `last_note`, so listings don't grow as notes accumulate.
- Notes stay with the occurrence they were written on: they move to completed history (visible in `list_completed`) and don't carry over to the next occurrence.

### Contract

- Inputs and outputs are snake_case (`due_date`, `series_id`, `month_anchor`, ...). Enum values are lowercase: `biannual`, `same_day`.
- `recurrence` and `month_anchor` are declared as JSON-schema `enum`s, so clients can validate before calling.
- Tools carry MCP annotations:
  - `list_tasks` and `list_completed` are read-only;
  - `delete_task` is destructive;
  - `update_task` is idempotent;
  - none of the tools are open-world.
- The persisted files under `/data` keep their original camelCase format; only the wire format is snake_case.

Argument handling is strict: every tool's input schema declares `additionalProperties: false`, and a call that passes an argument the tool doesn't declare fails with an error naming the unknown key(s) instead of being silently ignored. An enum argument with a value outside its allowed list fails with an error listing the allowed values. All failures (unknown arguments, invalid ids or dates, task not found) are returned as MCP tool errors (`isError: true`).

Set `TodoApp:DataPath` (env `TodoApp__DataPath`) to override the `/data` storage directory.

## Local Development

```bash
cd src/McpServer.TodoApp
mkdir -p /tmp/tododata

# The data path is fixed at /data — use Docker for local testing with volume mount.
dotnet run
```

## Docker Build & Run

```bash
# Build from repo root
docker build -f src/McpServer.TodoApp/Dockerfile -t rockylhotka/mcpserver-todoapp:latest .

# Push to Docker Hub
docker push rockylhotka/mcpserver-todoapp:latest

# Run with persistent storage
docker run -p 8080:8080 -v /tmp/tododata:/data rockylhotka/mcpserver-todoapp:latest

# Health check
curl http://localhost:8080/health

# MCP endpoint
curl http://localhost:8080/mcp
```

## Kubernetes Deployment

- **Namespace:** `rockbot`
- **Deployment:** `mcp-todo`
- **Image:** `rockylhotka/mcpserver-todoapp:latest`

To deploy a new image:

```bash
kubectl rollout restart deployment/mcp-todo -n rockbot
kubectl rollout status deployment/mcp-todo -n rockbot
```

Mount a Longhorn PVC at `/data` to persist `active.json` and `completed.json` across pod restarts:

```yaml
volumeMounts:
  - name: todo-data
    mountPath: /data
volumes:
  - name: todo-data
    persistentVolumeClaim:
      claimName: mcpserver-todoapp-pvc
```

No secrets or environment variables are required — all configuration is in `appsettings.json`.

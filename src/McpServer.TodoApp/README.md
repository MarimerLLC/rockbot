# McpServer.TodoApp

An MCP server providing a persistent to-do list accessible to AI agents via the Model Context Protocol. Runs as an ASP.NET Core 10 web app with JSON file storage at `/data`.

## Tools

| Tool | Parameters | Description |
|------|-----------|-------------|
| `add_task` | `title`, `due_date` (YYYY-MM-DD), optional `recurrence` (none/daily/weekly/monthly/quarterly/biannual/yearly), `description`, `recurrence_until`, `recurrence_count` | Adds a new task; returns created task as JSON |
| `list_tasks` | optional `due_before`, `due_after` (YYYY-MM-DD) | Lists active tasks, optionally filtered by due date |
| `complete_task` | `id` (GUID), optional `stop_recurrence` | Marks task complete; returns `{ completed, next, seriesEnded }` |
| `delete_task` | `id` (GUID) | Removes task from active list |
| `update_task` | `id`, optional `title`, `description`, `due_date`, `recurrence`, `recurrence_until`, `recurrence_count` | Updates fields on an active task; the id is preserved |
| `list_completed` | optional `completed_after`, `completed_before` (ISO datetime) | Lists completed tasks |

### Recurrence

- Next-due is calculated from the original due date, not the completion date.
- Every occurrence of a series shares a `seriesId` and carries its 1-based `occurrence` number. Tasks stored before series tracking existed get `seriesId = id` and start counting at 1.
- A series ends when:
  - `complete_task` is called with `stop_recurrence: true`;
  - the next occurrence would exceed `recurrence_count` (total occurrences, counting the first);
  - the next due date would fall after `recurrence_until` (inclusive).
- `complete_task` returns `next` (the new occurrence with its id, or `null`) and `seriesEnded` (`stop_recurrence` / `count_reached` / `until_reached`, or `null`).
- `update_task(recurrence: "none")` ends a series without deleting the task and clears its limits. `recurrence_until: ""` and `recurrence_count: 0` clear a single limit.
- Setting a limit on a task whose recurrence is `none` is an error.

Argument handling is strict: every tool's input schema declares `additionalProperties: false`, and a call that passes an argument the tool doesn't declare fails with an error naming the unknown key(s) instead of being silently ignored. All failures (unknown arguments, invalid ids or dates, task not found) are returned as MCP tool errors (`isError: true`).

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

# McpServer.TodoApp

An MCP server providing a persistent to-do list accessible to AI agents via the Model Context Protocol. Runs as an ASP.NET Core 10 web app with JSON file storage at `/data`.

## Tools

| Tool | Parameters | Description |
|------|-----------|-------------|
| `add_task` | `title`, `due_date` (YYYY-MM-DD), `recurrence` (none/daily/weekly/monthly/yearly), optional `description` | Adds a new task; returns created task as JSON |
| `list_tasks` | optional `due_before`, `due_after` (YYYY-MM-DD) | Lists active tasks, optionally filtered by due date |
| `complete_task` | `id` (GUID) | Marks task complete; repeating tasks auto-schedule next occurrence |
| `delete_task` | `id` (GUID) | Removes task from active list |
| `update_task` | `id`, optional `title`, `description`, `due_date` | Updates fields on an active task |
| `list_completed` | optional `completed_after`, `completed_before` (ISO datetime) | Lists completed tasks |

Recurrence next-due is calculated from the original due date, not the completion date.

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
docker build -f src/McpServer.TodoApp/Dockerfile -t mcpserver-todoapp .

# Run with persistent storage
docker run -p 8080:8080 -v /tmp/tododata:/data mcpserver-todoapp

# Health check
curl http://localhost:8080/health

# MCP endpoint
curl http://localhost:8080/mcp
```

## Kubernetes Deployment

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

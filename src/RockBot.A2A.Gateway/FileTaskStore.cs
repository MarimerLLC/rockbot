using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using A2A;

namespace RockBot.A2A.Gateway;

/// <summary>
/// File-backed <see cref="ITaskStore"/> that persists <see cref="AgentTask"/> records as JSON.
/// Each task is wrapped with caller identity and creation time for caller-scoped queries.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/> with serialized file writes.
/// </summary>
internal sealed class FileTaskStore : ITaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, StoredTask> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _filePath;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _loaded;

    public FileTaskStore(IHttpContextAccessor httpContextAccessor, string? filePath)
    {
        _httpContextAccessor = httpContextAccessor;
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
    }

    public async Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);

        return _tasks.TryGetValue(taskId, out var stored) ? stored.Task : null;
    }

    public async Task SaveTaskAsync(string taskId, AgentTask task, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);

        var callerId = _httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                       ?? "system";

        _tasks.AddOrUpdate(
            taskId,
            _ => new StoredTask(callerId, task, DateTimeOffset.UtcNow),
            (_, existing) => existing with { Task = task });

        await PersistAsync(cancellationToken);
    }

    public async Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (_tasks.TryRemove(taskId, out _))
            await PersistAsync(cancellationToken);
    }

    public async Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);

        IEnumerable<StoredTask> query = _tasks.Values;

        // Caller scoping via Tenant
        if (!string.IsNullOrWhiteSpace(request.Tenant))
            query = query.Where(s => string.Equals(s.CallerId, request.Tenant, StringComparison.OrdinalIgnoreCase));

        // Filter by context ID
        if (!string.IsNullOrWhiteSpace(request.ContextId))
            query = query.Where(s => s.Task.ContextId == request.ContextId);

        // Filter by status
        if (request.Status.HasValue)
            query = query.Where(s => s.Task.Status?.State == request.Status.Value);

        // Filter by timestamp
        if (request.StatusTimestampAfter.HasValue)
            query = query.Where(s => s.Task.Status?.Timestamp > request.StatusTimestampAfter.Value);

        // Order by creation time
        var ordered = query.OrderByDescending(s => s.CreatedAt).ToList();
        var totalSize = ordered.Count;

        // Cursor-based pagination (page token = task ID)
        if (!string.IsNullOrWhiteSpace(request.PageToken))
        {
            var idx = ordered.FindIndex(s => string.Equals(s.Task.Id, request.PageToken, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                ordered = ordered.Skip(idx + 1).ToList();
        }

        var pageSize = request.PageSize ?? 20;
        var page = ordered.Take(pageSize).ToList();
        var nextPageToken = page.Count == pageSize && ordered.Count > pageSize
            ? page[^1].Task.Id ?? string.Empty
            : string.Empty;

        // Apply history truncation and artifact filtering
        var tasks = page.Select(s =>
        {
            var task = s.Task;
            if (request.HistoryLength.HasValue && task.History is { Count: > 0 })
            {
                var maxHistory = request.HistoryLength.Value;
                if (task.History.Count > maxHistory)
                {
                    task = new AgentTask
                    {
                        Id = task.Id,
                        ContextId = task.ContextId,
                        Status = task.Status,
                        History = task.History.TakeLast(maxHistory).ToList(),
                        Artifacts = (request.IncludeArtifacts != false) ? task.Artifacts : null,
                        Metadata = task.Metadata
                    };
                }
            }
            else if (request.IncludeArtifacts == false && task.Artifacts is { Count: > 0 })
            {
                task = new AgentTask
                {
                    Id = task.Id,
                    ContextId = task.ContextId,
                    Status = task.Status,
                    History = task.History,
                    Artifacts = null,
                    Metadata = task.Metadata
                };
            }
            return task;
        }).ToList();

        return new ListTasksResponse
        {
            Tasks = tasks,
            NextPageToken = nextPageToken,
            PageSize = pageSize,
            TotalSize = totalSize
        };
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            if (_loaded) return;

            if (_filePath is not null && File.Exists(_filePath))
            {
                var json = await File.ReadAllTextAsync(_filePath, ct);
                var entries = JsonSerializer.Deserialize<List<StoredTask>>(json, JsonOptions);
                if (entries is not null)
                {
                    foreach (var entry in entries)
                    {
                        if (entry.Task?.Id is not null)
                            _tasks.TryAdd(entry.Task.Id, entry);
                    }
                }
            }

            _loaded = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_filePath is null) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            var entries = _tasks.Values.ToList();
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_filePath, json, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal sealed record StoredTask(string CallerId, AgentTask Task, DateTimeOffset CreatedAt);
}

using System.Text.Json;
using System.Text.Json.Serialization;
using McpServer.TodoApp.Models;

namespace McpServer.TodoApp.Services;

public sealed class TodoRepository
{
    private readonly string _activeFile;
    private readonly string _completedFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public TodoRepository(IConfiguration configuration)
    {
        var dataPath = configuration["TodoApp:DataPath"] ?? "/data";
        Directory.CreateDirectory(dataPath);
        _activeFile = Path.Combine(dataPath, "active.json");
        _completedFile = Path.Combine(dataPath, "completed.json");
    }

    public async Task<List<TodoItem>> GetActiveAsync()
    {
        await _lock.WaitAsync();
        try
        {
            // Items saved before series tracking existed have no SeriesId; each starts its own series.
            var items = await LoadAsync<List<TodoItem>>(_activeFile) ?? [];
            return items.ConvertAll(t => t.SeriesId is null ? t with { SeriesId = t.Id } : t);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<CompletedTodoItem>> GetCompletedAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var items = await LoadAsync<List<CompletedTodoItem>>(_completedFile) ?? [];
            return items.ConvertAll(t => t.SeriesId is null ? t with { SeriesId = t.Id } : t);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveActiveAsync(List<TodoItem> items)
    {
        await _lock.WaitAsync();
        try
        {
            await SaveAsync(_activeFile, items);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveCompletedAsync(List<CompletedTodoItem> items)
    {
        await _lock.WaitAsync();
        try
        {
            await SaveAsync(_completedFile, items);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<T?> LoadAsync<T>(string path)
    {
        if (!File.Exists(path))
            return default;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
    }

    private static async Task SaveAsync<T>(string path, T data)
    {
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
        }
        File.Move(temp, path, overwrite: true);
    }
}

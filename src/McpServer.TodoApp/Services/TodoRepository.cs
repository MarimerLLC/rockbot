using System.Text.Json;
using System.Text.Json.Serialization;
using McpServer.TodoApp.Models;

namespace McpServer.TodoApp.Services;

public sealed class TodoRepository
{
    private const string DataPath = "/data";
    private static readonly string ActiveFile = Path.Combine(DataPath, "active.json");
    private static readonly string CompletedFile = Path.Combine(DataPath, "completed.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public TodoRepository()
    {
        Directory.CreateDirectory(DataPath);
    }

    public async Task<List<TodoItem>> GetActiveAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await LoadAsync<List<TodoItem>>(ActiveFile) ?? [];
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
            return await LoadAsync<List<CompletedTodoItem>>(CompletedFile) ?? [];
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
            await SaveAsync(ActiveFile, items);
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
            await SaveAsync(CompletedFile, items);
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

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServer.TodoApp.Tests;

/// <summary>
/// Hosts the real TodoApp server in-process against a temp data directory and connects an MCP client to it.
/// </summary>
public abstract class TodoServerTestBase
{
    private WebApplicationFactory<Program>? _factory;
    private McpClient? _client;

    protected string DataPath { get; private set; } = null!;

    /// <summary>The MCP client; the server starts on first use, so tests can seed <see cref="DataPath"/> beforehand.</summary>
    protected async Task<McpClient> ClientAsync()
    {
        if (_client is not null)
            return _client;

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("TodoApp:DataPath", DataPath));

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_factory.Server.BaseAddress, "/") },
            _factory.CreateClient(),
            ownsHttpClient: true);
        _client = await McpClient.CreateAsync(transport);
        return _client;
    }

    [TestInitialize]
    public void InitializeDataPath()
    {
        DataPath = Path.Combine(Path.GetTempPath(), "todoapp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataPath);
    }

    [TestCleanup]
    public async Task CleanupServerAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        if (_factory is not null)
            await _factory.DisposeAsync();
        if (Directory.Exists(DataPath))
            Directory.Delete(DataPath, recursive: true);
    }

    protected async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?>? arguments = null) =>
        await (await ClientAsync()).CallToolAsync(tool, arguments);

    /// <summary>Calls a tool that is expected to succeed and returns its parsed JSON payload.</summary>
    protected async Task<JsonElement> CallOkAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await CallAsync(tool, arguments);
        Assert.IsFalse(result.IsError ?? false, $"{tool} failed: {TextOf(result)}");
        return JsonDocument.Parse(TextOf(result)).RootElement.Clone();
    }

    protected async Task<string> AddTaskAsync(string title, string dueDate = "2026-10-31", string recurrence = "none")
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = title,
            ["due_date"] = dueDate,
            ["recurrence"] = recurrence
        });
        return task.GetProperty("id").GetString()!;
    }

    protected Task<JsonElement> ListTasksAsync() => CallOkAsync("list_tasks");

    protected static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}

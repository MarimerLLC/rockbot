using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpServer.TodoApp.Tests;

[TestClass]
public sealed class TodoServerEndToEndTests
{
    private string _dataPath = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private McpClient _client = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "todoapp-tests", Guid.NewGuid().ToString("N"));
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("TodoApp:DataPath", _dataPath));

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_factory.Server.BaseAddress, "/") },
            _factory.CreateClient(),
            ownsHttpClient: true);
        _client = await McpClient.CreateAsync(transport);
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await _client.DisposeAsync();
        await _factory.DisposeAsync();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, recursive: true);
    }

    [TestMethod]
    public async Task UpdateTask_UnknownArgument_IsErrorAndTaskUnchanged()
    {
        var id = await AddTaskAsync("Pay invoice");

        var result = await _client.CallToolAsync("update_task", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["recurrence"] = "none"
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "'recurrence'");
        StringAssert.Contains(TextOf(result), "update_task");

        var tasks = await ListTasksAsync();
        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("none", tasks[0].GetProperty("recurrence").GetString());
    }

    [TestMethod]
    public async Task AddTask_UnknownArgumentWithRequiredPresent_IsErrorAndNothingAdded()
    {
        var result = await _client.CallToolAsync("add_task", new Dictionary<string, object?>
        {
            ["title"] = "Pay invoice",
            ["due_date"] = "2026-10-31",
            ["foo"] = "bar"
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "'foo'");
        Assert.AreEqual(0, (await ListTasksAsync()).GetArrayLength());
    }

    [TestMethod]
    public async Task AddTask_ValidArguments_Succeeds()
    {
        var result = await _client.CallToolAsync("add_task", new Dictionary<string, object?>
        {
            ["title"] = "Pay invoice",
            ["due_date"] = "2026-10-31",
            ["recurrence"] = "monthly"
        });

        Assert.IsFalse(result.IsError ?? false);
        Assert.AreEqual(1, (await ListTasksAsync()).GetArrayLength());
    }

    [TestMethod]
    public async Task ListTools_EveryToolDisallowsAdditionalProperties()
    {
        var tools = await _client.ListToolsAsync();

        Assert.IsTrue(tools.Count > 0);
        foreach (var tool in tools)
        {
            Assert.IsTrue(
                tool.ProtocolTool.InputSchema.TryGetProperty("additionalProperties", out var additional)
                    && additional.ValueKind == JsonValueKind.False,
                $"{tool.Name} does not set additionalProperties: false");
        }
    }

    [TestMethod]
    public async Task CompleteTask_InvalidId_IsError()
    {
        var result = await _client.CallToolAsync("complete_task", new Dictionary<string, object?> { ["id"] = "not-a-guid" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "invalid id");
    }

    [TestMethod]
    public async Task AddTask_InvalidDate_IsError()
    {
        var result = await _client.CallToolAsync("add_task", new Dictionary<string, object?>
        {
            ["title"] = "Pay invoice",
            ["due_date"] = "next tuesday"
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "invalid due_date");
    }

    private async Task<string> AddTaskAsync(string title)
    {
        var result = await _client.CallToolAsync("add_task", new Dictionary<string, object?>
        {
            ["title"] = title,
            ["due_date"] = "2026-10-31"
        });
        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        return JsonDocument.Parse(TextOf(result)).RootElement.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> ListTasksAsync()
    {
        var result = await _client.CallToolAsync("list_tasks");
        Assert.IsFalse(result.IsError ?? false, TextOf(result));
        return JsonDocument.Parse(TextOf(result)).RootElement;
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}

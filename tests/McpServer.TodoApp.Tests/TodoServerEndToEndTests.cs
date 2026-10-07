using System.Text.Json;

namespace McpServer.TodoApp.Tests;

[TestClass]
public sealed class TodoServerEndToEndTests : TodoServerTestBase
{
    [TestMethod]
    public async Task UpdateTask_UnknownArgument_IsErrorAndTaskUnchanged()
    {
        var id = await AddTaskAsync("Pay invoice");

        var result = await CallAsync("update_task", new()
        {
            ["id"] = id,
            ["recurrance"] = "none"
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "'recurrance'");
        StringAssert.Contains(TextOf(result), "update_task");

        var tasks = await ListTasksAsync();
        Assert.AreEqual(1, tasks.GetArrayLength());
        Assert.AreEqual("none", tasks[0].GetProperty("recurrence").GetString());
    }

    [TestMethod]
    public async Task AddTask_UnknownArgumentWithRequiredPresent_IsErrorAndNothingAdded()
    {
        var result = await CallAsync("add_task", new()
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
        await CallOkAsync("add_task", new()
        {
            ["title"] = "Pay invoice",
            ["due_date"] = "2026-10-31",
            ["recurrence"] = "monthly"
        });

        Assert.AreEqual(1, (await ListTasksAsync()).GetArrayLength());
    }

    [TestMethod]
    public async Task ListTools_EveryToolDisallowsAdditionalProperties()
    {
        var tools = await (await ClientAsync()).ListToolsAsync();

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
        var result = await CallAsync("complete_task", new() { ["id"] = "not-a-guid" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "invalid id");
    }

    [TestMethod]
    public async Task AddTask_InvalidDate_IsError()
    {
        var result = await CallAsync("add_task", new()
        {
            ["title"] = "Pay invoice",
            ["due_date"] = "next tuesday"
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "invalid due_date");
    }
}

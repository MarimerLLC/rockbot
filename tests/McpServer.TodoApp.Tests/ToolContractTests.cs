using System.Text.Json;
using ModelContextProtocol.Client;

namespace McpServer.TodoApp.Tests;

[TestClass]
public sealed class ToolContractTests : TodoServerTestBase
{
    private async Task<IDictionary<string, McpClientTool>> ToolsAsync() =>
        (await (await ClientAsync()).ListToolsAsync()).ToDictionary(t => t.Name);

    private static string[] EnumValues(McpClientTool tool, string property) =>
        tool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty(property).GetProperty("enum")
            .EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray();

    [TestMethod]
    public async Task Schema_RecurrenceIsLowercaseEnum()
    {
        var tools = await ToolsAsync();
        var expected = new[] { "none", "daily", "weekly", "monthly", "quarterly", "biannual", "yearly" };

        CollectionAssert.AreEquivalent(expected, EnumValues(tools["add_task"], "recurrence"));
        CollectionAssert.AreEquivalent(expected, EnumValues(tools["update_task"], "recurrence"));
    }

    [TestMethod]
    public async Task Schema_MonthAnchorIsEnum()
    {
        var tools = await ToolsAsync();

        CollectionAssert.AreEquivalent(new[] { "same_day", "last_day" }, EnumValues(tools["add_task"], "month_anchor"));
        CollectionAssert.AreEquivalent(new[] { "same_day", "last_day" }, EnumValues(tools["update_task"], "month_anchor"));
    }

    [TestMethod]
    public async Task Annotations_DescribeEachTool()
    {
        var tools = await ToolsAsync();

        Assert.IsTrue(tools["list_tasks"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.IsTrue(tools["list_completed"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.IsTrue(tools["delete_task"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.IsFalse(tools["add_task"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.IsFalse(tools["add_task"].ProtocolTool.Annotations?.IdempotentHint);
        Assert.IsFalse(tools["complete_task"].ProtocolTool.Annotations?.IdempotentHint);
        Assert.IsFalse(tools["add_task_note"].ProtocolTool.Annotations?.DestructiveHint);
        Assert.IsFalse(tools["add_task_note"].ProtocolTool.Annotations?.IdempotentHint);
        Assert.IsTrue(tools["update_task"].ProtocolTool.Annotations?.IdempotentHint);
        foreach (var tool in tools.Values)
            Assert.IsFalse(tool.ProtocolTool.Annotations?.OpenWorldHint, $"{tool.Name} should not be open-world");
    }

    [TestMethod]
    public async Task Output_IsSnakeCase_AndBiannualRoundTrips()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Filter",
            ["due_date"] = "2026-10-21",
            ["recurrence"] = "biannual"
        });

        Assert.AreEqual("biannual", task.GetProperty("recurrence").GetString());
        foreach (var name in new[] { "due_date", "created_at", "series_id", "recurrence_until", "recurrence_count", "month_anchor", "anchor_day" })
            Assert.IsTrue(task.TryGetProperty(name, out _), $"missing {name}");
        Assert.IsFalse(task.TryGetProperty("dueDate", out _));

        var result = await CallOkAsync("complete_task", new() { ["id"] = task.GetProperty("id").GetString() });
        Assert.IsTrue(result.TryGetProperty("series_ended", out _));
        Assert.IsTrue(result.GetProperty("completed").TryGetProperty("completed_at", out _));
        Assert.AreEqual("2027-04-21", result.GetProperty("next").GetProperty("due_date").GetString());
    }

    [TestMethod]
    public async Task Input_EnumValuesAreCaseInsensitive()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Invoice",
            ["due_date"] = "2026-10-15",
            ["recurrence"] = "Monthly"
        });

        Assert.AreEqual("monthly", task.GetProperty("recurrence").GetString());
    }

    [TestMethod]
    public async Task Input_InvalidRecurrence_NamesAllowedValues()
    {
        var result = await CallAsync("add_task", new()
        {
            ["title"] = "Invoice",
            ["due_date"] = "2026-10-15",
            ["recurrence"] = "fortnightly"
        });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "invalid value 'fortnightly' for recurrence");
        StringAssert.Contains(TextOf(result), "biannual");
    }

    [TestMethod]
    public async Task Store_KeepsCamelCaseFormat()
    {
        await AddTaskAsync("Filter", recurrence: "biannual");

        var stored = await File.ReadAllTextAsync(Path.Combine(DataPath, "active.json"));

        StringAssert.Contains(stored, "\"dueDate\"");
        StringAssert.Contains(stored, "\"biAnnual\"");
    }
}

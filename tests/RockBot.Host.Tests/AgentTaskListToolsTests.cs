using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

[TestClass]
public class AgentTaskListToolsTests
{
    private static AgentTaskListTools NewTools(out AgentTaskList list)
    {
        list = new AgentTaskList();
        return new AgentTaskListTools(list, NullLogger.Instance);
    }

    [TestMethod]
    public void Tools_ExposesTaskCreateAndTaskUpdate()
    {
        var tools = NewTools(out _);

        var names = tools.Tools.OfType<AIFunction>().Select(f => f.Name).ToHashSet();

        Assert.AreEqual(2, names.Count);
        Assert.IsTrue(names.Contains("task_create"),
            $"Expected a task_create-style tool. Got: {string.Join(", ", names)}");
        Assert.IsTrue(names.Contains("task_update"),
            $"Expected a task_update-style tool. Got: {string.Join(", ", names)}");
    }

    [TestMethod]
    public void TaskCreate_ReturnsConfirmationListingIds()
    {
        var tools = NewTools(out var list);

        var result = tools.TaskCreate(["read doc", "search emails"]);

        Assert.IsTrue(result.Contains("2 items"), $"Result missing item count: {result}");
        Assert.IsTrue(result.Contains("1.") && result.Contains("2."),
            $"Result missing assigned ids: {result}");
        Assert.IsTrue(result.Contains("read doc") && result.Contains("search emails"),
            $"Result missing item descriptions: {result}");

        // State updated.
        Assert.AreEqual(2, list.Snapshot().Count);
    }

    [TestMethod]
    public void TaskCreate_EmptyItems_ClearsAndReportsCleared()
    {
        var tools = NewTools(out var list);
        tools.TaskCreate(["a", "b"]);

        var result = tools.TaskCreate(Array.Empty<string>());

        Assert.AreEqual("Task list cleared.", result);
        Assert.IsTrue(list.IsEmpty);
    }

    [TestMethod]
    public void TaskCreate_NullItems_ClearsList()
    {
        var tools = NewTools(out var list);
        tools.TaskCreate(["a"]);

        var result = tools.TaskCreate(null!);

        Assert.AreEqual("Task list cleared.", result);
        Assert.IsTrue(list.IsEmpty);
    }

    [TestMethod]
    public void TaskUpdate_KnownId_UpdatesAndReturnsConfirmation()
    {
        var tools = NewTools(out var list);
        tools.TaskCreate(["one", "two"]);

        var result = tools.TaskUpdate(1, AgentTaskList.StatusInProgress);

        Assert.IsTrue(result.Contains("Task 1"), $"Missing task id in result: {result}");
        Assert.IsTrue(result.Contains(AgentTaskList.StatusInProgress),
            $"Missing status in result: {result}");
        Assert.AreEqual(AgentTaskList.StatusInProgress, list.Snapshot()[0].Status);
    }

    [TestMethod]
    public void TaskUpdate_UnknownId_ReturnsFriendlyError()
    {
        var tools = NewTools(out _);
        tools.TaskCreate(["only one"]);

        var result = tools.TaskUpdate(42, AgentTaskList.StatusCompleted);

        Assert.IsTrue(result.StartsWith("Error:"), $"Expected error prefix: {result}");
        Assert.IsTrue(result.Contains("42"), $"Should mention the bad id: {result}");
        Assert.IsTrue(result.Contains("1"), $"Should list known ids: {result}");
    }

    [TestMethod]
    public void TaskUpdate_EmptyTaskList_ReturnsHelpfulError()
    {
        var tools = NewTools(out _);

        var result = tools.TaskUpdate(1, AgentTaskList.StatusCompleted);

        Assert.IsTrue(result.StartsWith("Error:"), $"Expected error prefix: {result}");
        Assert.IsTrue(result.Contains("task_create"),
            $"Should hint to call task_create: {result}");
    }

    [TestMethod]
    public void TaskUpdate_InvalidStatus_ReturnsFriendlyError()
    {
        var tools = NewTools(out _);
        tools.TaskCreate(["a"]);

        var result = tools.TaskUpdate(1, "blocked");

        Assert.IsTrue(result.StartsWith("Error:"), $"Expected error prefix: {result}");
        Assert.IsTrue(result.Contains("blocked"),
            $"Should echo the bad status: {result}");
    }

    [TestMethod]
    public void TaskUpdate_BlankStatus_ReturnsFriendlyError()
    {
        var tools = NewTools(out _);
        tools.TaskCreate(["a"]);

        var result = tools.TaskUpdate(1, "   ");

        Assert.IsTrue(result.StartsWith("Error:"), $"Expected error prefix: {result}");
    }
}

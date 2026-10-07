using System.Text.Json;

namespace McpServer.TodoApp.Tests;

[TestClass]
public sealed class TaskNoteTests : TodoServerTestBase
{
    private Task<JsonElement> AddNoteAsync(string id, string text, string? source = null)
    {
        var args = new Dictionary<string, object?> { ["id"] = id, ["text"] = text };
        if (source is not null)
            args["source"] = source;
        return CallOkAsync("add_task_note", args);
    }

    [TestMethod]
    public async Task AddTaskNote_AppendsWithoutTouchingDescription()
    {
        var task = await CallOkAsync("add_task", new()
        {
            ["title"] = "Renew license",
            ["due_date"] = "2026-10-31",
            ["description"] = "Original intent"
        });
        var id = task.GetProperty("id").GetString()!;

        await AddNoteAsync(id, "re-verified on 2026-10-03", "heartbeat-patrol");
        var updated = await AddNoteAsync(id, "still pending");

        Assert.AreEqual("Original intent", updated.GetProperty("description").GetString());
        var notes = updated.GetProperty("notes");
        Assert.AreEqual(2, notes.GetArrayLength());
        Assert.AreEqual("re-verified on 2026-10-03", notes[0].GetProperty("text").GetString());
        Assert.AreEqual("heartbeat-patrol", notes[0].GetProperty("source").GetString());
        Assert.AreEqual(JsonValueKind.Null, notes[1].GetProperty("source").ValueKind);
        Assert.IsTrue(notes[0].TryGetProperty("at", out _));
    }

    [TestMethod]
    public async Task ListTasks_OmitsNotes_ShowsCountAndLast()
    {
        var id = await AddTaskAsync("Renew license");
        for (var i = 1; i <= 5; i++)
            await AddNoteAsync(id, $"check {i}");

        var listed = (await ListTasksAsync())[0];

        Assert.IsFalse(listed.TryGetProperty("notes", out _));
        Assert.AreEqual(5, listed.GetProperty("note_count").GetInt32());
        Assert.AreEqual("check 5", listed.GetProperty("last_note").GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task ListTasks_NoNotes_CountZeroLastNull()
    {
        await AddTaskAsync("Renew license");

        var listed = (await ListTasksAsync())[0];

        Assert.AreEqual(0, listed.GetProperty("note_count").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, listed.GetProperty("last_note").ValueKind);
    }

    [TestMethod]
    public async Task CompleteTask_NotesStayWithCompletedOccurrence()
    {
        var id = await AddTaskAsync("Invoice", recurrence: "monthly");
        await AddNoteAsync(id, "sent to client");

        var result = await CallOkAsync("complete_task", new() { ["id"] = id });

        Assert.AreEqual("sent to client", result.GetProperty("completed").GetProperty("notes")[0].GetProperty("text").GetString());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("next").GetProperty("notes").ValueKind);

        var completed = await CallOkAsync("list_completed");
        Assert.AreEqual(1, completed[0].GetProperty("notes").GetArrayLength());
    }

    [TestMethod]
    public async Task AddTaskNote_EmptyText_IsError()
    {
        var id = await AddTaskAsync("Renew license");

        var result = await CallAsync("add_task_note", new() { ["id"] = id, ["text"] = "  " });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "text must not be empty");
    }

    [TestMethod]
    public async Task AddTaskNote_UnknownTask_IsError()
    {
        var result = await CallAsync("add_task_note", new() { ["id"] = Guid.NewGuid().ToString(), ["text"] = "x" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "task not found");
    }

    [TestMethod]
    public async Task Notes_PersistInStore()
    {
        var id = await AddTaskAsync("Renew license");
        await AddNoteAsync(id, "persisted");

        var stored = await File.ReadAllTextAsync(Path.Combine(DataPath, "active.json"));

        StringAssert.Contains(stored, "\"notes\"");
        StringAssert.Contains(stored, "persisted");
    }
}

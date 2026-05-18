using System.Text.Json;
using RockBot.Host;

namespace RockBot.Subagent.Tests;

[TestClass]
public class SubagentFailureDetailsTests
{
    [TestMethod]
    public void BuildFailureDetailsPayload_TimeoutWithFullDiagnostics_IncludesEverythingPrimaryNeeds()
    {
        var startedAt = DateTimeOffset.Parse("2026-05-18T15:17:49Z");
        var elapsed = TimeSpan.FromMinutes(14).Add(TimeSpan.FromSeconds(58.453));
        var timeout = TimeSpan.FromMinutes(15);

        var diag = new LoopDiagnostics
        {
            Iterations = 22,
            ToolCalls = 47,
            LastAssistantText = "Switching to OneDrive default save location and then inspecting returned paths.",
            LastToolName = "spawn_wisps",
            LastToolArguments = "definitions=[{\"description\":\"Download Teams bridge May 18…\"}]",
            LastToolStatus = "in-flight",
            LastToolResult = null,
            LastToolStartedAt = startedAt + TimeSpan.FromMinutes(14),
            LastToolCompletedAt = null,
        };

        var progress = new (DateTimeOffset At, string Message)[]
        {
            (startedAt + TimeSpan.FromMinutes(1), "Services confirmed: calendar-mcp and onedrive-personal."),
            (startedAt + TimeSpan.FromMinutes(8), "Initial email sweep completed."),
            (startedAt + TimeSpan.FromMinutes(13), "Teams archive search found two Xebia Teams JSON files."),
        };

        var json = SubagentRunner.BuildFailureDetailsPayload(
            taskId: "f57cb6b15c6b",
            description: "Daily Operational Brief email/Teams communications evidence slice.",
            reason: "timeout",
            errorMessage: "Timed out after 15 minutes",
            startedAt: startedAt,
            elapsed: elapsed,
            timeout: timeout,
            diagnostics: diag,
            recentProgress: progress);

        // Parse it back so the assertions check semantic shape, not exact formatting.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual("f57cb6b15c6b", root.GetProperty("taskId").GetString());
        Assert.AreEqual("timeout", root.GetProperty("reason").GetString());
        Assert.AreEqual("Timed out after 15 minutes", root.GetProperty("error").GetString());
        Assert.AreEqual(22, root.GetProperty("iterations").GetInt32());
        Assert.AreEqual(47, root.GetProperty("toolCalls").GetInt32());
        Assert.AreEqual(15.0, root.GetProperty("timeoutMinutes").GetDouble());

        var lastTool = root.GetProperty("lastTool");
        Assert.AreEqual("spawn_wisps", lastTool.GetProperty("name").GetString());
        Assert.AreEqual("in-flight", lastTool.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, lastTool.GetProperty("completedAt").ValueKind);

        StringAssert.Contains(
            root.GetProperty("lastAssistantText").GetString() ?? string.Empty,
            "OneDrive default save location");

        var progressArr = root.GetProperty("recentProgress");
        Assert.AreEqual(3, progressArr.GetArrayLength());
        StringAssert.Contains(
            progressArr[2].GetProperty("message").GetString() ?? string.Empty,
            "Teams archive search");
    }

    [TestMethod]
    public void BuildFailureDetailsPayload_WhenNoToolEverRan_LastToolIsNull()
    {
        var diag = new LoopDiagnostics
        {
            Iterations = 1,
            ToolCalls = 0,
            LastAssistantText = "Starting work.",
        };

        var json = SubagentRunner.BuildFailureDetailsPayload(
            taskId: "abc123",
            description: "test",
            reason: "exception",
            errorMessage: "Boom",
            startedAt: DateTimeOffset.UtcNow,
            elapsed: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            diagnostics: diag,
            recentProgress: Array.Empty<(DateTimeOffset, string)>());

        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("lastTool").ValueKind);
        Assert.AreEqual(0, doc.RootElement.GetProperty("recentProgress").GetArrayLength());
    }

    [TestMethod]
    public void BuildFailureDetailsPayload_TruncatesLongDescription()
    {
        var longDescription = new string('x', 1500);

        var json = SubagentRunner.BuildFailureDetailsPayload(
            taskId: "t1",
            description: longDescription,
            reason: "cancelled",
            errorMessage: null,
            startedAt: DateTimeOffset.UtcNow,
            elapsed: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromMinutes(5),
            diagnostics: new LoopDiagnostics(),
            recentProgress: Array.Empty<(DateTimeOffset, string)>());

        using var doc = JsonDocument.Parse(json);
        var stored = doc.RootElement.GetProperty("description").GetString() ?? string.Empty;
        Assert.IsTrue(stored.Length < longDescription.Length,
            $"Expected truncation, got {stored.Length} chars");
        StringAssert.EndsWith(stored, "…");
    }
}

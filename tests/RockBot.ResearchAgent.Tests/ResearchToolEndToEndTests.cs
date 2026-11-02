using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace RockBot.ResearchAgent.Tests;

/// <summary>
/// The <c>research</c> tool over real streamable HTTP, both ways it can be called: as a plain call
/// (a clarifying question goes through MRTR, <see cref="InputRequiredException"/>) and as a task
/// (<c>CallToolWithPollingAsync</c>; the question goes through <c>ElicitAsync</c> and the task
/// store). If <see cref="ResearchCallMode"/> picks the wrong way, these fail: a task that throws
/// <see cref="InputRequiredException"/> fails on the server, and a plain call can't
/// <c>ElicitAsync</c> on a stateless server, so the question would never reach the client.
/// </summary>
[TestClass]
public class ResearchToolEndToEndTests
{
    private static string Text(CallToolResult result, int index = 0) =>
        ((TextContentBlock)result.Content[index]).Text;

    private static Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> Answer(
        string action, string? choice, List<ElicitRequestParams?>? seen = null, TimeSpan? delay = null) =>
        async (request, ct) =>
        {
            seen?.Add(request);
            if (delay is { } d)
                await Task.Delay(d, ct);
            return new ElicitResult
            {
                Action = action,
                Content = choice is null ? null : new Dictionary<string, JsonElement>
                {
                    [ResearchTools.AnswerKey] = JsonSerializer.SerializeToElement(choice),
                },
            };
        };

    [TestMethod]
    public async Task AClearQuestion_IsResearchedAsAsked_WithSourcesAndStructuredContent()
    {
        var runner = new FakeResearchRunner();
        var scoper = FakeScoper.Clear();
        await using var server = await ResearchServerHarness.StartAsync(runner, scoper);
        var seen = new List<ElicitRequestParams?>();
        var client = await server.ConnectAsync(Answer("accept", "planet", seen));

        var result = await client.CallToolAsync(
            ResearchTools.ToolName,
            new Dictionary<string, object?> { ["question"] = "How far is Mars?", ["context"] = "school project", ["constraints"] = "since 2020" });

        Assert.IsFalse(result.IsError ?? false, Text(result));
        Assert.AreEqual("answer to How far is Mars? [as asked]", Text(result));
        StringAssert.Contains(Text(result, 1), "Sources consulted:");
        StringAssert.Contains(Text(result, 1), "Page A — https://example.com/a");
        StringAssert.Contains(Text(result, 1), "- https://example.com/b");

        Assert.IsNotNull(result.StructuredContent);
        var structured = JsonSerializer.SerializeToElement(result.StructuredContent);
        Assert.AreEqual("answer to How far is Mars? [as asked]", structured.GetProperty("answer").GetString());
        Assert.AreEqual(2, structured.GetProperty("sources").GetArrayLength());
        Assert.AreEqual("https://example.com/a", structured.GetProperty("sources")[0].GetProperty("url").GetString());
        Assert.IsFalse(structured.TryGetProperty("interpretation", out _));

        Assert.AreEqual(0, seen.Count, "a clear question asks nothing");
        Assert.AreEqual(1, scoper.Calls);
        var request = runner.Requests.Single();
        Assert.AreEqual("school project", request.Context);
        Assert.AreEqual("since 2020", request.Constraints);
        Assert.IsNull(request.Interpretation);
    }

    [TestMethod]
    public async Task APlainCall_AsksOnceThroughMrtr_AndResearchesTheChosenReading()
    {
        var runner = new FakeResearchRunner();
        var scoper = FakeScoper.Mercury();
        await using var server = await ResearchServerHarness.StartAsync(runner, scoper);
        var seen = new List<ElicitRequestParams?>();
        var client = await server.ConnectAsync(Answer("accept", "element", seen));

        var result = await client.CallToolAsync(ResearchTools.ToolName, new Dictionary<string, object?> { ["question"] = "Mercury" });

        Assert.IsFalse(result.IsError ?? false, Text(result));
        Assert.AreEqual(1, seen.Count, "exactly one question");
        StringAssert.Contains(seen[0]!.Message, "Mercury");
        var field = seen[0]!.RequestedSchema.Properties[ResearchTools.AnswerKey];
        Assert.IsInstanceOfType<ElicitRequestParams.TitledSingleSelectEnumSchema>(field);
        Assert.AreEqual(1, seen[0]!.RequestedSchema.Properties.Count, "a single choice, no free text");
        CollectionAssert.AreEqual(
            new[] { "planet", "element", "band" },
            ((ElicitRequestParams.TitledSingleSelectEnumSchema)field).OneOf.Select(o => o.Const).ToArray());

        StringAssert.StartsWith(Text(result), "Researched as: Mercury, the chemical element");
        Assert.AreEqual("Mercury, the chemical element", runner.Requests.Single().Interpretation);
        Assert.AreEqual(1, scoper.Calls, "the retried call skips scoping: the scope rode in the request state");

        var structured = JsonSerializer.SerializeToElement(result.StructuredContent);
        Assert.AreEqual("Mercury, the chemical element", structured.GetProperty("interpretation").GetString());
        Assert.IsTrue(structured.GetProperty("interpretationChosen").GetBoolean());
    }

    [TestMethod]
    [DataRow("decline")]
    [DataRow("cancel")]
    public async Task ADeclinedQuestion_ResearchesTheMostLikelyReading_AndSaysSo(string action)
    {
        var runner = new FakeResearchRunner();
        await using var server = await ResearchServerHarness.StartAsync(runner, FakeScoper.Mercury());
        var client = await server.ConnectAsync(Answer(action, choice: null));

        var result = await client.CallToolAsync(ResearchTools.ToolName, new Dictionary<string, object?> { ["question"] = "Mercury" });

        Assert.IsFalse(result.IsError ?? false, Text(result));
        StringAssert.StartsWith(Text(result), "Interpreted as: Mercury, the planet");
        Assert.AreEqual("Mercury, the planet", runner.Requests.Single().Interpretation);
        var structured = JsonSerializer.SerializeToElement(result.StructuredContent);
        Assert.IsFalse(structured.GetProperty("interpretationChosen").GetBoolean());
    }

    [TestMethod]
    public async Task AnAnswerOutsideTheChoices_IsTreatedAsNoChoice()
    {
        var runner = new FakeResearchRunner();
        await using var server = await ResearchServerHarness.StartAsync(runner, FakeScoper.Mercury());
        var client = await server.ConnectAsync(Answer("accept", "freddie"));

        var result = await client.CallToolAsync(ResearchTools.ToolName, new Dictionary<string, object?> { ["question"] = "Mercury" });

        StringAssert.StartsWith(Text(result), "Interpreted as: Mercury, the planet");
    }

    [TestMethod]
    public async Task AnExpiredRequestState_IsRescoped_AndNotAskedAgain()
    {
        var runner = new FakeResearchRunner();
        var scoper = FakeScoper.Mercury();
        await using var server = await ResearchServerHarness.StartAsync(runner, scoper, scopeTokenLifetime: TimeSpan.FromMilliseconds(1));
        var seen = new List<ElicitRequestParams?>();
        // The answer comes back after the token has expired.
        var client = await server.ConnectAsync(Answer("accept", "band", seen, delay: TimeSpan.FromMilliseconds(200)));

        var result = await client.CallToolAsync(ResearchTools.ToolName, new Dictionary<string, object?> { ["question"] = "Mercury" });

        Assert.IsFalse(result.IsError ?? false, Text(result));
        Assert.AreEqual(1, seen.Count, "the retried call never asks a second time");
        Assert.AreEqual(2, scoper.Calls, "the expired state was thrown away and the question scoped again");
        StringAssert.StartsWith(Text(result), "Interpreted as: Mercury, the planet");
    }

    [TestMethod]
    public async Task AClientWithoutAnElicitationHandler_GetsTheMostLikelyReading()
    {
        var runner = new FakeResearchRunner();
        await using var server = await ResearchServerHarness.StartAsync(runner, FakeScoper.Mercury());
        var client = await server.ConnectAsync(elicit: null);

        var result = await client.CallToolAsync(ResearchTools.ToolName, new Dictionary<string, object?> { ["question"] = "Mercury" });

        Assert.IsFalse(result.IsError ?? false, Text(result));
        StringAssert.StartsWith(Text(result), "Interpreted as: Mercury, the planet");
    }

    [TestMethod]
    public async Task AsATask_ALongRunReturnsATask_AndCompletesThroughPolling()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeResearchRunner { Hold = hold };
        await using var server = await ResearchServerHarness.StartAsync(runner, FakeScoper.Clear());
        var client = await server.ConnectAsync();

        var started = await client.CallToolAsTaskAsync(ResearchServerHarness.Call("How far is Mars?"));

        Assert.IsTrue(started.IsTask, "a task-aware call gets a task back at once");
        var taskId = started.TaskCreated.TaskId;
        Assert.IsInstanceOfType<WorkingTaskResult>(await client.GetTaskAsync(taskId));

        hold.SetResult();
        CallToolResult? done = null;
        for (var i = 0; i < 200 && done is null; i++)
        {
            if (await client.GetTaskAsync(taskId) is CompletedTaskResult completed)
                done = completed.Result.Deserialize<CallToolResult>(ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            else
                await Task.Delay(20);
        }

        Assert.IsNotNull(done, "the task completed");
        Assert.AreEqual("answer to How far is Mars? [as asked]", Text(done));
    }

    [TestMethod]
    public async Task CallToolWithPolling_AsksThroughTheTask_AndResearchesTheChosenReading()
    {
        var runner = new FakeResearchRunner();
        var scoper = FakeScoper.Mercury();
        await using var server = await ResearchServerHarness.StartAsync(runner, scoper);
        var seen = new List<ElicitRequestParams?>();
        var client = await server.ConnectAsync(Answer("accept", "band", seen));

        // The council's way of calling: CallToolWithPollingAsync opts into tasks, so research runs
        // as a task and its question goes through the task store (input_required, tasks/update).
        var result = await client.CallToolWithPollingAsync(ResearchServerHarness.Call("Mercury"));

        Assert.IsFalse(result.IsError ?? false, Text(result));
        Assert.AreEqual(1, seen.Count, "exactly one question");
        StringAssert.StartsWith(Text(result), "Researched as: Mercury Rev, the band");
        Assert.AreEqual("Mercury Rev, the band", runner.Requests.Single().Interpretation);
        Assert.AreEqual(1, scoper.Calls);
    }

    [TestMethod]
    public async Task CallToolWithPolling_ADeclinedQuestion_StillCompletes()
    {
        var runner = new FakeResearchRunner();
        await using var server = await ResearchServerHarness.StartAsync(runner, FakeScoper.Mercury());
        var client = await server.ConnectAsync(Answer("decline", choice: null));

        var result = await client.CallToolWithPollingAsync(ResearchServerHarness.Call("Mercury"));

        StringAssert.StartsWith(Text(result), "Interpreted as: Mercury, the planet");
    }

    [TestMethod]
    public async Task ABurstOfCalls_NeverRunsMoreThanTheCap()
    {
        var inner = new FakeResearchRunner { Delay = TimeSpan.FromMilliseconds(150) };
        using var gate = new ResearchConcurrencyGate(2);
        await using var server = await ResearchServerHarness.StartAsync(new GatedResearchRunner(inner, gate), FakeScoper.Clear());
        var client = await server.ConnectAsync();

        var calls = Enumerable.Range(0, 6).Select(i => client.CallToolAsync(
            ResearchTools.ToolName, new Dictionary<string, object?> { ["question"] = $"q{i}" }).AsTask()).ToArray();
        var results = await Task.WhenAll(calls);

        Assert.IsTrue(results.All(r => !(r.IsError ?? false)));
        Assert.AreEqual(6, inner.Requests.Count);
        Assert.AreEqual(2, inner.Peak, "two at a time, never more");
        Assert.AreEqual(0, gate.Running);
    }

    [TestMethod]
    public async Task AnEmptyQuestion_IsAnError()
    {
        await using var server = await ResearchServerHarness.StartAsync(new FakeResearchRunner(), FakeScoper.Clear());
        var client = await server.ConnectAsync();

        var result = await client.CallToolAsync(ResearchTools.ToolName, new Dictionary<string, object?> { ["question"] = "  " });

        Assert.IsTrue(result.IsError);
    }
}

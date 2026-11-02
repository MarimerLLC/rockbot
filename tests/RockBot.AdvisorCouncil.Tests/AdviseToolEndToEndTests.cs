using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil.Tests;

/// <summary>
/// The <c>advise</c> tool over real streamable HTTP, both ways it can be called: as a plain call
/// (the Select step's question goes through MRTR, <see cref="InputRequiredException"/>) and as a
/// task (the question goes through <c>ElicitAsync</c> and the task store). If
/// <see cref="CouncilCallMode"/> picks the wrong way these fail: a task that throws
/// <see cref="InputRequiredException"/> fails on the server, and a plain call can't
/// <c>ElicitAsync</c> on a stateless server.
/// </summary>
[TestClass]
public class AdviseToolEndToEndTests
{
    private static string Text(CallToolResult result, int index = 0) =>
        ((TextContentBlock)result.Content[index]).Text;

    private static Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> Answer(
        string action, IReadOnlyDictionary<string, string>? answers = null, List<ElicitRequestParams?>? seen = null, TimeSpan? delay = null) =>
        async (request, ct) =>
        {
            if (seen is not null)
                lock (seen) seen.Add(request);
            if (delay is { } d)
                await Task.Delay(d, ct);
            return new ElicitResult
            {
                Action = action,
                Content = answers?.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value)),
            };
        };

    private static readonly Dictionary<string, string> Answers = new()
    {
        [CouncilChoices.PreferredOptionField] = "option_2",
        [CouncilChoices.TimeHorizonField] = "years",
        [CouncilChoices.RiskAppetiteField] = "low",
    };

    private static Dictionary<string, object?> Args(string question, string[]? options = null, Dictionary<string, object?>? more = null)
    {
        var args = new Dictionary<string, object?> { ["question"] = question };
        if (options is not null)
            args["options"] = options;
        foreach (var (k, v) in more ?? [])
            args[k] = v;
        return args;
    }

    [TestMethod]
    public async Task AClearQuestion_Deliberates_WithTextAndStructuredContent()
    {
        var council = FakeCouncil.Clear();
        await using var server = await CouncilServerHarness.StartAsync(council);
        var seen = new List<ElicitRequestParams?>();
        var client = await server.ConnectAsync(Answer("accept", Answers, seen));

        var result = await client.CallToolAsync(CouncilTools.ToolName, Args("Adopt Rust?", more: new()
        {
            ["context"] = "Team of four, C# shop",
            ["time_horizon"] = "Years",
            ["risk_appetite"] = "nonsense",
            ["critique"] = "on",
            ["personas"] = new[] { "skeptic" },
        }));

        Assert.IsFalse(result.IsError ?? false, Text(result));
        StringAssert.StartsWith(Text(result), "Advice on Adopt Rust?");
        Assert.AreEqual(0, seen.Count, "nothing open, nothing asked");

        var request = council.Deliberations.Single();
        Assert.AreEqual("Team of four, C# shop", request.Context);
        Assert.AreEqual("years", request.TimeHorizon, "enum values are normalised");
        Assert.IsNull(request.RiskAppetite, "an unknown enum value is dropped, not an error");
        Assert.AreEqual(CouncilToggle.On, request.Critique);
        CollectionAssert.AreEqual(new[] { "skeptic" }, request.Personas.ToArray());
        Assert.AreEqual(ClarificationStatus.NotAsked, request.Clarification);

        var structured = JsonSerializer.SerializeToElement(result.StructuredContent);
        Assert.AreEqual("Adopt Rust?", structured.GetProperty("question").GetString());
        Assert.AreEqual("medium", structured.GetProperty("confidence").GetString());
        Assert.AreEqual(2, structured.GetProperty("personas").GetArrayLength());
        Assert.AreEqual(0, structured.GetProperty("assumptions").GetArrayLength());
        Assert.AreEqual("not_asked", structured.GetProperty("metadata").GetProperty("clarification").GetString());
    }

    [TestMethod]
    public async Task APlainCall_AsksOnceThroughMrtr_AndTheAnswersReachTheDeliberation()
    {
        var council = FakeCouncil.Open();
        await using var server = await CouncilServerHarness.StartAsync(council);
        var seen = new List<ElicitRequestParams?>();
        var client = await server.ConnectAsync(Answer("accept", Answers, seen));

        var result = await client.CallToolAsync(CouncilTools.ToolName, Args("Postgres or Mongo?", ["Postgres", "Mongo"]));

        Assert.IsFalse(result.IsError ?? false, Text(result));
        Assert.AreEqual(1, seen.Count, "exactly one question");
        var schema = seen[0]!.RequestedSchema!;
        CollectionAssert.AreEquivalent(
            new[] { CouncilChoices.PreferredOptionField, CouncilChoices.TimeHorizonField, CouncilChoices.RiskAppetiteField },
            schema.Properties.Keys.ToArray());
        Assert.IsTrue(schema.Properties.Values.All(p => p is ElicitRequestParams.TitledSingleSelectEnumSchema),
            "choice fields only: no free text, no yes/no");
        var preferred = (ElicitRequestParams.TitledSingleSelectEnumSchema)schema.Properties[CouncilChoices.PreferredOptionField];
        CollectionAssert.AreEqual(new[] { "Postgres", "Mongo" }, preferred.OneOf.Select(o => o.Title).ToArray());

        Assert.AreEqual(1, council.Selects, "the retried call skips Select: the selection rode in the request state");
        var request = council.Deliberations.Single();
        Assert.AreEqual("Mongo", request.PreferredOption);
        Assert.AreEqual("years", request.TimeHorizon);
        Assert.AreEqual("low", request.RiskAppetite);
        Assert.AreEqual(0, request.Unresolved.Count);
        Assert.AreEqual(ClarificationStatus.Answered, request.Clarification);
    }

    [TestMethod]
    public async Task SuppliedFields_AreNotAskedAbout()
    {
        var council = FakeCouncil.Open();
        await using var server = await CouncilServerHarness.StartAsync(council);
        var seen = new List<ElicitRequestParams?>();
        var client = await server.ConnectAsync(Answer("accept", Answers, seen));

        // One option only (nothing to lean toward), and the time horizon supplied: only risk appetite is open.
        await client.CallToolAsync(CouncilTools.ToolName, Args("Move to Rust?", ["Rust"], new() { ["time_horizon"] = "months" }));

        Assert.AreEqual(1, seen.Count);
        CollectionAssert.AreEqual(new[] { CouncilChoices.RiskAppetiteField }, seen[0]!.RequestedSchema!.Properties.Keys.ToArray());
        var request = council.Deliberations.Single();
        Assert.AreEqual("months", request.TimeHorizon);
        Assert.AreEqual("low", request.RiskAppetite);
    }

    [TestMethod]
    [DataRow("decline")]
    [DataRow("cancel")]
    public async Task ADeclinedQuestion_Deliberates_AndTheOpenPointsBecomeAssumptions(string action)
    {
        var council = FakeCouncil.Open();
        await using var server = await CouncilServerHarness.StartAsync(council);
        var client = await server.ConnectAsync(Answer(action));

        var result = await client.CallToolAsync(CouncilTools.ToolName, Args("Postgres or Mongo?", ["Postgres", "Mongo"]));

        Assert.IsFalse(result.IsError ?? false, Text(result));
        var request = council.Deliberations.Single();
        Assert.AreEqual(ClarificationStatus.Declined, request.Clarification);
        Assert.AreEqual(3, request.Unresolved.Count);
        StringAssert.Contains(Text(result), "**Assumptions**");
        StringAssert.Contains(Text(result), "Assumed a typical risk appetite.");
        var structured = JsonSerializer.SerializeToElement(result.StructuredContent);
        Assert.AreEqual(3, structured.GetProperty("assumptions").GetArrayLength());
        Assert.AreEqual("declined", structured.GetProperty("metadata").GetProperty("clarification").GetString());
    }

    [TestMethod]
    public async Task APartialAnswer_KeepsTheRestAsAssumptions()
    {
        var council = FakeCouncil.Open();
        await using var server = await CouncilServerHarness.StartAsync(council);
        var client = await server.ConnectAsync(Answer("accept", new Dictionary<string, string>
        {
            [CouncilChoices.TimeHorizonField] = "weeks",
            [CouncilChoices.RiskAppetiteField] = "reckless", // not a choice
        }));

        await client.CallToolAsync(CouncilTools.ToolName, Args("Ship Friday?"));

        var request = council.Deliberations.Single();
        Assert.AreEqual("weeks", request.TimeHorizon);
        Assert.IsNull(request.RiskAppetite);
        CollectionAssert.AreEqual(new[] { CouncilChoices.RiskAppetiteField }, request.Unresolved.ToArray());
        Assert.AreEqual(ClarificationStatus.Answered, request.Clarification);
    }

    [TestMethod]
    public async Task AClientWithoutAnElicitationHandler_IsNeverAsked()
    {
        var council = FakeCouncil.Open();
        await using var server = await CouncilServerHarness.StartAsync(council);
        var client = await server.ConnectAsync(elicit: null);

        var result = await client.CallToolAsync(CouncilTools.ToolName, Args("Ship Friday?"));

        Assert.IsFalse(result.IsError ?? false, Text(result));
        var request = council.Deliberations.Single();
        Assert.AreEqual(ClarificationStatus.NotAsked, request.Clarification);
        Assert.AreEqual(2, request.Unresolved.Count);
    }

    [TestMethod]
    public async Task ATooThinQuestion_ReturnsEarly_NamingTheContextItNeeds()
    {
        var council = FakeCouncil.TooThin();
        await using var server = await CouncilServerHarness.StartAsync(council);
        var seen = new List<ElicitRequestParams?>();
        var client = await server.ConnectAsync(Answer("accept", Answers, seen));

        var result = await client.CallToolAsync(CouncilTools.ToolName, Args("Should I do it?"));

        Assert.IsFalse(result.IsError ?? false, "an early return is a normal result, not an error");
        Assert.AreEqual(0, council.Deliberations.Count, "nothing expensive ran");
        Assert.AreEqual(0, seen.Count, "free text is never elicited");
        StringAssert.Contains(Text(result), "`context`");
        StringAssert.Contains(Text(result), "- what 'it' is");
        var structured = JsonSerializer.SerializeToElement(result.StructuredContent);
        Assert.AreEqual("needs_context", structured.GetProperty("status").GetString());
        Assert.AreEqual(2, structured.GetProperty("needs").GetArrayLength());
    }

    [TestMethod]
    public async Task AnExpiredRequestState_SelectsAgain_AndDoesNotAskTwice()
    {
        var council = FakeCouncil.Open();
        await using var server = await CouncilServerHarness.StartAsync(council, tokenLifetime: TimeSpan.FromMilliseconds(1));
        var seen = new List<ElicitRequestParams?>();
        // The answer comes back after the token has expired.
        var client = await server.ConnectAsync(Answer("accept", Answers, seen, delay: TimeSpan.FromMilliseconds(200)));

        var result = await client.CallToolAsync(CouncilTools.ToolName, Args("Ship Friday?"));

        Assert.IsFalse(result.IsError ?? false, Text(result));
        Assert.AreEqual(1, seen.Count, "the retried call never asks a second time");
        Assert.AreEqual(2, council.Selects, "the expired state was thrown away and Select ran again");
        var request = council.Deliberations.Single();
        Assert.IsNull(request.TimeHorizon, "the late answer is not trusted");
        Assert.AreEqual(ClarificationStatus.NotAsked, request.Clarification);
    }

    [TestMethod]
    public async Task ATamperedRequestState_IsIgnored()
    {
        var council = FakeCouncil.Open();
        await using var server = await CouncilServerHarness.StartAsync(council);
        var client = await server.ConnectAsync(Answer("accept", Answers));

        var call = CouncilServerHarness.Call("Ship Friday?");
        call.RequestState = "not-a-real-token";
        var result = await client.CallToolAsync(call);

        Assert.IsFalse(result.IsError ?? false, Text(result));
        Assert.AreEqual(1, council.Selects);
        Assert.AreEqual(ClarificationStatus.NotAsked, council.Deliberations.Single().Clarification);
    }

    [TestMethod]
    public async Task AsATask_ALongRunReturnsATask_AndCompletesThroughPolling()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var council = new FakeCouncil(FakeCouncil.Selection()) { Hold = hold };
        await using var server = await CouncilServerHarness.StartAsync(council);
        var client = await server.ConnectAsync();

        var started = await client.CallToolAsTaskAsync(CouncilServerHarness.Call("Adopt Rust?"));

        Assert.IsTrue(started.IsTask, "a task-aware call gets a task back at once");
        var taskId = started.TaskCreated!.TaskId;
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
        StringAssert.StartsWith(Text(done), "Advice on Adopt Rust?");
    }

    [TestMethod]
    public async Task AsATask_TheSelectQuestionGoesThroughTheTask()
    {
        var council = FakeCouncil.Open();
        await using var server = await CouncilServerHarness.StartAsync(council);
        var seen = new List<ElicitRequestParams?>();
        var client = await server.ConnectAsync(Answer("accept", Answers, seen));

        // CallToolWithPollingAsync opts into tasks: the council runs as a task and asks through
        // the task store (input_required, tasks/update).
        var result = await client.CallToolWithPollingAsync(CouncilServerHarness.Call("Postgres or Mongo?", ["Postgres", "Mongo"]));

        Assert.IsFalse(result.IsError ?? false, Text(result));
        Assert.AreEqual(1, seen.Count, "exactly one question");
        Assert.AreEqual(1, council.Selects);
        var request = council.Deliberations.Single();
        Assert.AreEqual("Mongo", request.PreferredOption);
        Assert.AreEqual(ClarificationStatus.Answered, request.Clarification);
    }

    [TestMethod]
    public async Task ABurstOfCalls_NeverDeliberatesMoreThanTheCap()
    {
        var inner = new FakeCouncil(FakeCouncil.Selection()) { Delay = TimeSpan.FromMilliseconds(150) };
        using var gate = new CouncilConcurrencyGate(2);
        await using var server = await CouncilServerHarness.StartAsync(new GatedCouncil(inner, gate));
        var client = await server.ConnectAsync();

        var calls = Enumerable.Range(0, 5).Select(i => client.CallToolAsync(
            CouncilTools.ToolName, Args($"q{i}")).AsTask()).ToArray();
        var results = await Task.WhenAll(calls);

        Assert.IsTrue(results.All(r => !(r.IsError ?? false)));
        Assert.AreEqual(5, inner.Deliberations.Count);
        Assert.AreEqual(2, inner.Peak, "two at a time, never more");
        Assert.AreEqual(0, gate.Running);
    }

    [TestMethod]
    public async Task AnEmptyQuestion_IsAnError()
    {
        await using var server = await CouncilServerHarness.StartAsync(FakeCouncil.Clear());
        var client = await server.ConnectAsync();

        var result = await client.CallToolAsync(CouncilTools.ToolName, Args("  "));

        Assert.IsTrue(result.IsError);
    }

    [TestMethod]
    public async Task ToolsList_ShowsAdviseWithItsArguments()
    {
        await using var server = await CouncilServerHarness.StartAsync(FakeCouncil.Clear());
        var client = await server.ConnectAsync();

        var tool = (await client.ListToolsAsync()).Single();

        Assert.AreEqual("advise", tool.Name);
        var properties = tool.JsonSchema.GetProperty("properties");
        foreach (var name in new[] { "question", "context", "options", "time_horizon", "risk_appetite", "reversibility", "personas", "critique", "pre_research" })
            Assert.IsTrue(properties.TryGetProperty(name, out _), $"missing argument {name}");
        CollectionAssert.AreEqual(new[] { "question" },
            tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
    }
}

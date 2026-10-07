using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.McpMeasure;
using RockBot.McpMeasure.Fixtures;
using RockBot.McpMeasure.Hosting;
using RockBot.McpMeasure.Measurement;
using RockBot.Llm;
using RockBot.Tools.Mcp;

namespace RockBot.McpMeasure.Tests;

/// <summary>
/// The measurement harness (#613): the grader's failure taxonomy against the fixture servers'
/// real schemas, and the whole pipeline — real agent loop, gateway, bridge, fixture servers —
/// driven by the scripted model in every mode, so the harness stays reproducible without a key.
/// </summary>
[TestClass]
public class MeasureHarnessTests
{
    private static FixtureHost _fixtures = null!;
    private static MeasureRig _eager = null!;
    private static Grader _grader = null!;

    private static readonly MeasureTurn SendEmail = Turn("send_email");
    private static readonly MeasureTurn MarimerFiles = Turn("list_files_marimer");

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _fixtures = await FixtureHost.StartAsync();
        _eager = await MeasureRig.StartAsync(_fixtures, McpWrapperMode.Eager, NullLoggerFactory.Instance, CancellationToken.None);
        _grader = new Grader(_fixtures.Servers, _eager.Catalog);
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _eager.DisposeAsync();
        await _fixtures.DisposeAsync();
    }

    private static MeasureTurn Turn(string taskId) => MeasureTasks.All.Single(t => t.Id == taskId).Turns[0];

    private static FunctionCallContent Call(string name, Dictionary<string, object?> args) => new("c", name, args);

    private static Dictionary<string, object?> Invoke(string server, string tool, object? arguments = null)
    {
        var args = new Dictionary<string, object?> { ["server_name"] = server, ["tool_name"] = tool };
        if (arguments is not null)
            args["arguments"] = arguments;
        return args;
    }

    private static readonly Dictionary<string, object?> GoodEmail = new()
    {
        ["to"] = new[] { "alice@example.com" },
        ["subject"] = "Lunch Thursday?",
        ["body"] = "Are you free for lunch on Thursday at noon?"
    };

    // ── Grader ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void TypedCall_WithTheTasksArguments_IsCorrect()
    {
        var grade = _grader.Grade(SendEmail, [Call("adjutant__send_email", GoodEmail)], "Sent.");

        Assert.IsTrue(grade.FirstCallCorrect);
        Assert.IsNull(grade.FirstCallFailure);
        Assert.AreEqual("typed", grade.Attempts[0].Via);
    }

    [TestMethod]
    public void InvokeTool_NestedUnderAnyAlias_OrFlattened_IsCorrect()
    {
        foreach (var alias in new[] { "arguments", "params", "args" })
        {
            var args = new Dictionary<string, object?> { ["server_name"] = "Adjutant", ["tool_name"] = "send_email", [alias] = GoodEmail };
            Assert.IsTrue(_grader.Grade(SendEmail, [Call("mcp_invoke_tool", args)], null).FirstCallCorrect, alias);
        }

        var flat = new Dictionary<string, object?>(GoodEmail) { ["server_name"] = "adjutant", ["tool_name"] = "send_email" };
        Assert.IsTrue(_grader.Grade(SendEmail, [Call("mcp_invoke_tool", flat)], null).FirstCallCorrect, "flattened");
    }

    [TestMethod]
    public void MetaCalls_AreNotDownstreamAttempts()
    {
        var grade = _grader.Grade(SendEmail,
            [Call("mcp_list_services", []), Call("mcp_get_service_details", new() { ["server_name"] = "adjutant" }),
             Call("adjutant__send_email", GoodEmail)], null);

        Assert.AreEqual(2, grade.MetaCalls);
        Assert.AreEqual(1, grade.Attempts.Count);
        Assert.IsTrue(grade.FirstCallCorrect);
    }

    [TestMethod]
    public void Taxonomy_ClassifiesEachFailure()
    {
        (string Name, Dictionary<string, object?> Args, string Expected)[] cases =
        [
            ("mcp_invoke_tool", [], Outcome.EmptyArguments),
            ("mcp_invoke_tool", Invoke("adjutant", "send_email"), Outcome.EmptyArguments),
            ("mcp_invoke_tool", Invoke("adjutant", "adjutant__send_email", GoodEmail), Outcome.TypedNameAsToolName),
            ("mcp_invoke_tool", Invoke("calendar", "send_email", GoodEmail), Outcome.InventedServer),
            ("mcp_invoke_tool", Invoke("adjutant", "reply_to_email", GoodEmail), Outcome.InventedTool),
            ("mcp_invoke_tool", Invoke("adjutant", "send_email", new Dictionary<string, object?> { ["recipients"] = "alice@example.com", ["subject"] = "x" }), Outcome.WrongKey),
            ("mcp_invoke_tool", Invoke("adjutant", "send_email", new Dictionary<string, object?> { ["to"] = "alice@example.com", ["subject"] = "x" }), Outcome.WrongType),
            ("mcp_invoke_tool", Invoke("adjutant", "send_email", "{\"to\":[\"alice@example.com\"]}"), Outcome.WrongType),
            ("adjutant__send_email", new() { ["to"] = new[] { "bob@example.com" }, ["subject"] = "x", ["body"] = "y" }, Outcome.WrongValue),
            ("adjutant__get_emails", [], Outcome.OtherTool),
            ("adjutant__email_send", GoodEmail, Outcome.InventedTool),
            ("mail__send_email", GoodEmail, Outcome.InventedServer),
            ("send_email", GoodEmail, Outcome.BareToolName),
        ];

        foreach (var (name, args, expected) in cases)
        {
            var grade = _grader.Grade(SendEmail, [Call(name, args)], null);
            Assert.IsFalse(grade.FirstCallCorrect, $"{name} {expected}");
            Assert.AreEqual(expected, grade.FirstCallFailure, $"{name}({System.Text.Json.JsonSerializer.Serialize(args)})");
        }
    }

    [TestMethod]
    public void LookAlikeServer_IsWrongServer()
    {
        var grade = _grader.Grade(MarimerFiles,
            [Call("onedrive-personal__list_files", new() { ["folder_path"] = "Documents/Reports" })], null);

        Assert.IsTrue(grade.FirstCallWellFormed);
        Assert.AreEqual(Outcome.WrongServer, grade.FirstCallFailure);
    }

    [TestMethod]
    public void NoCall_IsToolCallAsText_WhenTheReplyWritesOneOut()
    {
        Assert.AreEqual(Outcome.ToolCallAsText,
            _grader.Grade(SendEmail, [], """{"name": "adjutant__send_email", "arguments": {"to": ["alice@example.com"]}}""").FirstCallFailure);
        Assert.AreEqual(Outcome.ToolCallAsText,
            _grader.Grade(SendEmail, [], "mcp_invoke_tool(server_name=\"adjutant\", tool_name=\"send_email\")").FirstCallFailure);
        Assert.AreEqual(Outcome.NoAttempt,
            _grader.Grade(SendEmail, [], "I can't send email.").FirstCallFailure);
    }

    // ── End to end, scripted ──────────────────────────────────────────────────

    [TestMethod]
    [DataRow(McpWrapperMode.Off)]
    [DataRow(McpWrapperMode.Eager)]
    [DataRow(McpWrapperMode.Lazy)]
    [DataRow(McpWrapperMode.Pinned)]
    public async Task Scripted_EveryTask_IsCorrectAndCompleted(McpWrapperMode mode)
    {
        var rows = await RunScriptedAsync(mode);

        Assert.AreEqual(MeasureTasks.All.Sum(t => t.Turns.Count), rows.Count);
        foreach (var row in rows)
        {
            Assert.IsNull(row.Error, $"{row.Task}: {row.Error}");
            Assert.IsTrue(row.FirstCallCorrect, $"{row.Task}: {row.Failure}");
            Assert.IsTrue(row.Completed, $"{row.Task}: the fixture never received a valid call.");
        }
    }

    [TestMethod]
    public async Task Scripted_SecondTurnOnTheSameServer_PinnedNeedsNoSearch_LazyDoes()
    {
        var lazy = (await RunScriptedAsync(McpWrapperMode.Lazy)).Single(r => r.Task == "reply_thread#2");
        var pinned = (await RunScriptedAsync(McpWrapperMode.Pinned)).Single(r => r.Task == "reply_thread#2");

        Assert.AreEqual(1, lazy.MetaCalls, "Lazy: send_email wasn't activated by the first turn's search.");
        Assert.AreEqual(0, pinned.MetaCalls, "Pinned: the first turn's call pinned all of adjutant's tools.");
    }

    [TestMethod]
    public void Report_SummarizesRates()
    {
        TurnResult Row(bool correct, string? failure) => new()
        {
            Model = "low", ModelId = "m", Tier = "Low", Mode = "Eager", Task = "send_email", Run = 1,
            FirstCallCorrect = correct, FirstCallWellFormed = correct, Completed = correct, Failure = failure
        };

        var summary = Report.Summarize([Row(true, null), Row(false, Outcome.WrongKey)], "# t");

        StringAssert.Contains(summary, "| low (m) | Eager | 2 | 0% | 50% |", "No attempts recorded, so no target call was right.");
        StringAssert.Contains(summary, "| send_email |");
        StringAssert.Contains(summary, Outcome.WrongKey);
    }

    [TestMethod]
    public void TargetRightFirstTime_IgnoresPreparatoryCalls_ButNotAWrongFirstTry()
    {
        DownstreamAttempt Attempt(string tool, string args, string outcome = Outcome.Ok) =>
            new("typed", $"adjutant__{tool}", "adjutant", tool, args, outcome, null);
        TurnResult Row(params DownstreamAttempt[] attempts) => new()
        {
            Model = "m", ModelId = "m", Tier = "Low", Mode = "Eager", Task = "reply_thread#2", Run = 1, Attempts = [.. attempts]
        };
        var good = $$"""{"to":["{{FixtureServers.DanaAddress}}"],"subject":"Re","body":"Count me in."}""";

        Assert.IsTrue(Report.TargetRightFirstTime(Row(Attempt("search_emails", """{"query":"Dana"}"""), Attempt("send_email", good))));
        Assert.IsFalse(Report.TargetRightFirstTime(Row(Attempt("send_email", """{"to":"dana","subject":"Re"}""", Outcome.WrongType),
            Attempt("send_email", good))));
        Assert.IsFalse(Report.TargetRightFirstTime(Row(Attempt("search_emails", """{"query":"Dana"}"""))));
    }

    private static async Task<IReadOnlyList<TurnResult>> RunScriptedAsync(McpWrapperMode mode)
    {
        await using var rig = await MeasureRig.StartAsync(_fixtures, mode, NullLoggerFactory.Instance, CancellationToken.None);
        var grader = new Grader(_fixtures.Servers, rig.Catalog);
        var runner = new Runner(_fixtures, NullLoggerFactory.Instance);
        var model = new ModelUnderTest("scripted", "Balanced", ScriptedChatClient.ModelId, new ScriptedChatClient(), ModelBehavior.Default);
        var settings = new RunSettings(10, Prompts.Minimal(), Verbose: false);

        var rows = new List<TurnResult>();
        foreach (var task in MeasureTasks.All)
            rows.AddRange(await runner.RunAsync(rig, grader, model, task, 1, settings, CancellationToken.None));
        return rows;
    }
}

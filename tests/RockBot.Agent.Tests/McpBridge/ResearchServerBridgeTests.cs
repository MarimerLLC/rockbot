extern alias research;

using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Extensions.Tasks;
using research::RockBot.ResearchAgent;
using RockBot.Agent.McpBridge.Tasks;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// The research server's real <c>research</c> tool (#717) behind the real bridge with
/// <c>"tasks": true</c>, as the primary agent will call it: the call starts as a background task,
/// the scoping pass's one question is handed back to the conversation, the answer lets the task
/// finish, and the result is delivered later. The runner and scoper are fakes; the tool, the
/// Tasks extension and the bridge are real.
/// </summary>
[TestClass]
public class ResearchServerBridgeTests
{
    private const string Session = "session/test";

    private sealed class Runner : IResearchRunner
    {
        public List<ResearchRequest> Requests { get; } = [];

        public Task<ResearchOutcome> RunAsync(
            ResearchRequest request, string runId, Func<string, CancellationToken, Task>? onProgress, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(new ResearchOutcome(
                $"Mercury findings ({request.Interpretation ?? "as asked"})",
                [new ResearchSource("https://example.com/mercury", "Mercury")]));
        }
    }

    private sealed class Scoper(bool ambiguous) : IResearchScoper
    {
        public Task<ResearchScope> ScopeAsync(ResearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ambiguous
                ? new ResearchScope(
                    [new("planet", "Mercury, the planet"), new("element", "Mercury, the chemical element"), new("band", "Mercury Rev, the band")],
                    "planet")
                : ResearchScope.Clear);
    }

    private static Task<BridgeHarness> StartAsync(Runner runner, bool ambiguous, McpElicitationConfig elicitation)
    {
        var tool = new ResearchTools(
            runner, new Scoper(ambiguous),
            new ResearchScopeToken(new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(10)),
            NullLogger<ResearchTools>.Instance).CreateTool();

        return BridgeHarness.StartAsync(
            [tool],
            configure: e =>
            {
                e.Tasks = true;
                e.Elicitation = elicitation;
            },
            configureBridge: o => o.TaskPollMinMs = 10,
            taskStore: new InMemoryMcpTaskStore { DefaultPollIntervalMs = 20 },
            configureTasks: t => t.ExecutionModeSelector = ResearchCallMode.Select);
    }

    private static string StartedTaskId(ToolInvokeResponse response)
    {
        Assert.IsFalse(response.IsError, response.Content);
        StringAssert.Contains(response.Content, "started as a background task");
        var match = System.Text.RegularExpressions.Regex.Match(response.Content ?? "", "t_[0-9a-f]{24}");
        Assert.IsTrue(match.Success, response.Content);
        return match.Value;
    }

    [TestMethod]
    public async Task TheScopingQuestion_IsHandedBack_AndTheAnswerFinishesTheResearch()
    {
        var runner = new Runner();
        await using var harness = await StartAsync(runner, ambiguous: true,
            new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });

        var taskId = StartedTaskId(await harness.InvokeAsync(
            ResearchTools.ToolName, """{"question":"Mercury"}""", sessionId: Session, canAnswer: true, canReceiveLater: true));
        Assert.AreEqual(0, runner.Requests.Count, "no research before the question is answered");

        var question = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindQuestion);
        Assert.AreEqual(taskId, question.TaskId);
        StringAssert.Contains(question.Content, "could mean different things");
        StringAssert.Contains(question.Content, "Mercury, the chemical element");

        var answer = await harness.AnswerAsync(question.QuestionId!, """{"interpretation":"element"}""", sessionId: Session);
        Assert.IsNull(answer.Error, answer.Error);
        Assert.IsTrue(answer.TaskContinues);

        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        Assert.AreEqual(taskId, result.TaskId);
        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "Researched as: Mercury, the chemical element");
        StringAssert.Contains(result.Content, "Mercury findings (Mercury, the chemical element)");
        StringAssert.Contains(result.Content, "https://example.com/mercury");
        Assert.AreEqual("Mercury, the chemical element", runner.Requests.Single().Interpretation);
    }

    [TestMethod]
    public async Task ADeclinedQuestion_StillDeliversResearch_OnTheMostLikelyReading()
    {
        var runner = new Runner();
        await using var harness = await StartAsync(runner, ambiguous: true,
            new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });

        StartedTaskId(await harness.InvokeAsync(
            ResearchTools.ToolName, """{"question":"Mercury"}""", sessionId: Session, canAnswer: true, canReceiveLater: true));
        var question = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindQuestion);

        await harness.AnswerAsync(question.QuestionId!, answersJson: null, decline: true, sessionId: Session);

        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        StringAssert.Contains(result.Content, "Interpreted as: Mercury, the planet");
    }

    [TestMethod]
    public async Task AServerDefault_AnswersTheQuestionInBand()
    {
        var runner = new Runner();
        await using var harness = await StartAsync(runner, ambiguous: true, new McpElicitationConfig
        {
            Defaults = new() { ["interpretation"] = JsonSerializer.SerializeToElement("band") },
        });

        StartedTaskId(await harness.InvokeAsync(
            ResearchTools.ToolName, """{"question":"Mercury"}""", sessionId: Session, canAnswer: true, canReceiveLater: true));

        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        StringAssert.Contains(result.Content, "Researched as: Mercury Rev, the band");
        Assert.IsFalse(harness.TaskResults.Any(r => r.Kind == McpTaskResult.KindQuestion));
    }

    [TestMethod]
    public async Task AClearQuestion_RunsWithoutAsking()
    {
        var runner = new Runner();
        await using var harness = await StartAsync(runner, ambiguous: false,
            new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });

        StartedTaskId(await harness.InvokeAsync(
            ResearchTools.ToolName, """{"question":"How far is Mercury from the Sun?"}""", sessionId: Session, canAnswer: true, canReceiveLater: true));

        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        StringAssert.Contains(result.Content, "Mercury findings (as asked)");
        Assert.IsFalse(harness.TaskResults.Any(r => r.Kind == McpTaskResult.KindQuestion));
    }

    [TestMethod]
    public async Task ACallerThatCantTakeALaterResult_GetsTheAnswerInline()
    {
        var runner = new Runner();
        await using var harness = await StartAsync(runner, ambiguous: false,
            new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });

        // No rb-mcp-tasks header: the bridge makes a plain call, and research runs synchronously.
        var response = await harness.InvokeAsync(ResearchTools.ToolName, """{"question":"Mercury"}""", sessionId: Session);

        Assert.IsFalse(response.IsError, response.Content);
        StringAssert.Contains(response.Content, "Mercury findings (as asked)");
        Assert.AreEqual(0, harness.TaskResults.Count);
    }
}

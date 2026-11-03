extern alias council;

using System.Text.Json;
using council::RockBot.AdvisorCouncil;
using council::RockBot.AdvisorCouncil.Council;
using council::RockBot.AdvisorCouncil.Schema;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Extensions.Tasks;
using RockBot.Agent.McpBridge.Tasks;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// The advisor council's real <c>advise</c> tool (#758) behind the real bridge with
/// <c>"tasks": true</c>, as the primary agent calls it: the call starts as a background task, the
/// Select step's one question is handed back to the conversation, the answer lets the task
/// finish, and the result is delivered later as a turn. The council behind the tool is a fake;
/// the tool, the Tasks extension and the bridge are real.
/// </summary>
[TestClass]
public class AdvisorCouncilBridgeTests
{
    private const string Session = "session/test";

    private sealed class Council(IReadOnlyList<string> missing) : ICouncil
    {
        public List<CouncilRequest> Deliberations { get; } = [];

        public Task<SelectorOutput> SelectAsync(CouncilRequest request, CancellationToken ct) =>
            Task.FromResult(new SelectorOutput(
                [new SelectedPersona("skeptic"), new SelectedPersona("engineer")], false, false, "fake",
                [.. missing.Where(m => !request.Has(m))]));

        public Task<CouncilResponse> DeliberateAsync(CouncilRequest request, SelectorOutput selection, string runId, CancellationToken ct)
        {
            lock (Deliberations) Deliberations.Add(request);
            return Task.FromResult(new CouncilResponse(
                request.Question,
                [new PersonaView("skeptic", "v", [], []), new PersonaView("engineer", "v", [], [])],
                [],
                $"Council advice (horizon={request.TimeHorizon ?? "?"}, risk={request.RiskAppetite ?? "?"})",
                "medium",
                new CouncilMetadata(false, false, 2, 1, 4, "hash", "fake", 2, [], request.Clarification),
                [.. request.Unresolved.Select(u => $"Assumed a typical {u.Replace('_', ' ')}.")]));
        }
    }

    private static readonly string[] Open = [CouncilChoices.TimeHorizonField, CouncilChoices.RiskAppetiteField];

    private static Task<BridgeHarness> StartAsync(Council council, McpElicitationConfig elicitation)
    {
        var tool = new CouncilTools(
            council,
            new CouncilClarificationToken(new EphemeralDataProtectionProvider(), TimeSpan.FromMinutes(10)),
            Options.Create(new CouncilOptions()),
            NullLogger<CouncilTools>.Instance).CreateTool();

        return BridgeHarness.StartAsync(
            [tool],
            configure: e =>
            {
                e.Tasks = true;
                e.Elicitation = elicitation;
            },
            configureBridge: o => o.TaskPollMinMs = 10,
            taskStore: new InMemoryMcpTaskStore { DefaultPollIntervalMs = 20 },
            configureTasks: t => t.ExecutionModeSelector = CouncilCallMode.Select);
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
    public async Task TheSelectQuestion_IsHandedBack_AndTheAnswerFinishesTheCouncil()
    {
        var council = new Council(Open);
        await using var harness = await StartAsync(council,
            new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });

        var taskId = StartedTaskId(await harness.InvokeAsync(
            CouncilTools.ToolName, """{"question":"Should we rewrite the billing service?"}""",
            sessionId: Session, canAnswer: true, canReceiveLater: true));
        Assert.AreEqual(0, council.Deliberations.Count, "no deliberation before the question is answered");

        var question = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindQuestion);
        Assert.AreEqual(taskId, question.TaskId);
        StringAssert.Contains(question.Content, "advisor council");
        StringAssert.Contains(question.Content, "time_horizon");
        StringAssert.Contains(question.Content, "risk_appetite");

        var answer = await harness.AnswerAsync(question.QuestionId!,
            """{"time_horizon":"years","risk_appetite":"low"}""", sessionId: Session);
        Assert.IsNull(answer.Error, answer.Error);
        Assert.IsTrue(answer.TaskContinues);

        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        Assert.AreEqual(taskId, result.TaskId);
        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "Council advice (horizon=years, risk=low)");

        var deliberated = council.Deliberations.Single();
        Assert.AreEqual(ClarificationStatus.Answered, deliberated.Clarification);
        Assert.AreEqual(0, deliberated.Unresolved.Count);
    }

    [TestMethod]
    public async Task ADeclinedQuestion_StillDeliversAdvice_WithAssumptions()
    {
        var council = new Council(Open);
        await using var harness = await StartAsync(council,
            new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });

        StartedTaskId(await harness.InvokeAsync(
            CouncilTools.ToolName, """{"question":"Should we rewrite the billing service?"}""",
            sessionId: Session, canAnswer: true, canReceiveLater: true));
        var question = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindQuestion);

        await harness.AnswerAsync(question.QuestionId!, answersJson: null, decline: true, sessionId: Session);

        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        Assert.IsFalse(result.IsError, result.Content);
        StringAssert.Contains(result.Content, "**Assumptions**");
        StringAssert.Contains(result.Content, "Assumed a typical time horizon.");
        Assert.AreEqual(ClarificationStatus.Declined, council.Deliberations.Single().Clarification);
    }

    [TestMethod]
    public async Task ServerDefaults_AnswerTheQuestionInBand()
    {
        var council = new Council(Open);
        await using var harness = await StartAsync(council, new McpElicitationConfig
        {
            Defaults = new()
            {
                ["time_horizon"] = JsonSerializer.SerializeToElement("months"),
                ["risk_appetite"] = JsonSerializer.SerializeToElement("moderate"),
            },
        });

        StartedTaskId(await harness.InvokeAsync(
            CouncilTools.ToolName, """{"question":"Should we rewrite the billing service?"}""",
            sessionId: Session, canAnswer: true, canReceiveLater: true));

        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        StringAssert.Contains(result.Content, "Council advice (horizon=months, risk=moderate)");
        Assert.IsFalse(harness.TaskResults.Any(r => r.Kind == McpTaskResult.KindQuestion));
    }

    [TestMethod]
    public async Task SuppliedArguments_RunWithoutAsking()
    {
        var council = new Council(Open);
        await using var harness = await StartAsync(council,
            new McpElicitationConfig { Mode = McpElicitationConfig.ModeHandback });

        StartedTaskId(await harness.InvokeAsync(
            CouncilTools.ToolName,
            """{"question":"Should we rewrite the billing service?","context":"Legacy VB6, 3 devs","time_horizon":"years","risk_appetite":"high"}""",
            sessionId: Session, canAnswer: true, canReceiveLater: true));

        var result = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindCompleted);
        StringAssert.Contains(result.Content, "Council advice (horizon=years, risk=high)");
        Assert.IsFalse(harness.TaskResults.Any(r => r.Kind == McpTaskResult.KindQuestion));
        Assert.AreEqual("Legacy VB6, 3 devs", council.Deliberations.Single().Context);
    }
}

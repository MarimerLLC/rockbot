using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RockBot.Llm;

namespace RockBot.Host.Tests;

/// <summary>
/// The consequential-action gate's second opinion: before refusing a call the regex classifier
/// read as information-only, the gate asks a decision model (Microsoft-Decision-1) whether the
/// user asked for the change. 0.16.14-preview.2 refused "mark them complete now" because the verb
/// list had no "mark".
/// </summary>
[TestClass]
public class DecisionModelGateTests
{
    private const string CompleteTask = "todo__complete_task";
    // No verb the regex knows: "is in" and "got ordered" read as information-only.
    private const string MarkThemComplete = "the furnace filter is in and the propane got ordered yesterday";

    private static IDictionary<string, object?> State(DecisionRequest request) =>
        (IDictionary<string, object?>)request.State;

    private static ActionGateScope InformationOnly(string userMessage = MarkThemComplete, string? previous = null) =>
        new(RunOrigin.UserTurn, UserRequestKind.InformationOnly) { Request = new ActionGateRequest(userMessage, previous) };

    private static ConsequentialActionGate Gate(FakeDecisionService decisions,
        Action<ConsequentialActionGateOptions>? configure = null)
    {
        var host = new AgentHostOptions();
        configure?.Invoke(host.ConsequentialActionGate);
        return new ConsequentialActionGate(Options.Create(host), decisions: decisions);
    }

    // ── Gate decisions ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task ModelSaysUserAsked_CallRuns()
    {
        var decisions = new FakeDecisionService(0.99);

        Assert.IsNull(await Gate(decisions).CheckAsync(CompleteTask, "id=1", InformationOnly()));
        var request = decisions.Requests.Single();
        Assert.AreEqual(DecisionPurposes.ActionGate, request.Purpose);
        Assert.AreEqual(MarkThemComplete, State(request)["user_message"]);
        var action = (IDictionary<string, object?>)State(request)["proposed_action"]!;
        Assert.AreEqual(CompleteTask, action["tool"]);
        Assert.AreEqual("todo", action["system_changed"]);
    }

    [TestMethod]
    public async Task ModelSaysUserDidNotAsk_CallIsRefused()
    {
        var refusal = await Gate(new FakeDecisionService(0.01)).CheckAsync(CompleteTask, "id=1", InformationOnly("why did it refuse?"));

        Assert.IsTrue(ConsequentialActions.IsRefusal(refusal), refusal);
    }

    [TestMethod]
    public async Task Threshold_IsConfigurable()
    {
        var scope = InformationOnly();

        var strict = new FakeDecisionService(0.6);
        strict.Thresholds[DecisionPurposes.ActionGate] = 0.7;

        Assert.IsNotNull(await Gate(strict).CheckAsync(CompleteTask, null, scope));
        Assert.IsNull(await Gate(new FakeDecisionService(0.6)).CheckAsync(CompleteTask, null, InformationOnly()),
            "default threshold 0.5");
    }

    [TestMethod]
    [DataRow(true, DisplayName = "allow on failure (default)")]
    [DataRow(false, DisplayName = "refuse on failure")]
    public async Task ModelFails_FollowsAllowOnFailure(bool allowOnFailure)
    {
        var result = await Gate(new FakeDecisionService(null), o => o.AllowOnDecisionFailure = allowOnFailure)
            .CheckAsync(CompleteTask, null, InformationOnly());

        Assert.AreEqual(allowOnFailure, result is null);
    }

    [TestMethod]
    public async Task ModelNotConfigured_RegexAloneDecides()
    {
        var decisions = new FakeDecisionService(0.99) { Enabled = false };

        Assert.IsNotNull(await Gate(decisions).CheckAsync(CompleteTask, null, InformationOnly()));
        Assert.AreEqual(0, decisions.Requests.Count);
    }

    [TestMethod]
    public async Task RegexAlreadyAllows_ModelNotAsked()
    {
        var decisions = new FakeDecisionService(0.01);
        var instructed = new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.Instruction)
        {
            Request = new ActionGateRequest("mark them complete", null),
        };

        Assert.IsNull(await Gate(decisions).CheckAsync(CompleteTask, null, instructed));
        Assert.IsNull(await Gate(decisions).CheckAsync("todo__list_tasks", null, InformationOnly()), "reads are never gated");
        Assert.AreEqual(0, decisions.Requests.Count);
    }

    [TestMethod]
    public async Task NoUserRequest_ModelNotAsked()
    {
        var decisions = new FakeDecisionService(0.99);

        Assert.IsNotNull(await Gate(decisions).CheckAsync(CompleteTask, null,
            new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.InformationOnly)));
        Assert.AreEqual(0, decisions.Requests.Count);
    }

    // ── Caching and subagents ───────────────────────────────────────────────

    [TestMethod]
    public async Task OneQuestionPerToolPerTurn_SharedWithSubagents()
    {
        var decisions = new FakeDecisionService(0.99);
        var gate = Gate(decisions);
        var turn = InformationOnly();
        var subagent = turn.ForSubagent();

        Assert.AreSame(turn.Request, subagent.Request, "a subagent judges its parent's user message");
        Assert.IsNull(await gate.CheckAsync(CompleteTask, "id=1", turn));
        Assert.IsNull(await gate.CheckAsync(CompleteTask, "id=2", subagent));
        Assert.AreEqual(1, decisions.Requests.Count, "the second completion reuses the first answer");

        await gate.CheckAsync("calendar-mcp__create_event", null, subagent);
        Assert.AreEqual(2, decisions.Requests.Count, "a different change is asked about separately");
    }

    [TestMethod]
    public void ScopeEquality_IgnoresNoRequest_ButNeverMergesTwoTurns()
    {
        Assert.AreEqual(new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.Instruction),
            new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.Instruction));
        Assert.AreNotEqual(new ActionGateRequest("x", null), new ActionGateRequest("x", null));
    }

    // ── In the loop ─────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(false, DisplayName = "native")]
    [DataRow(true, DisplayName = "text-based")]
    public async Task UserTurn_RegexMissesTheRequest_ModelAllows_ToolRuns(bool textBased)
    {
        var decisions = new FakeDecisionService(0.99);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "You are a test agent."),
            new(ChatRole.User, "what's overdue?"),
            new(ChatRole.Assistant, "Install furnace filter and Order propane are overdue."),
            new(ChatRole.User, MarkThemComplete),
        ];

        var run = await ConsequentialActionGateTests.RunWithToolAsync(textBased, messages, CompleteTask,
            actionGate: ActionGateScope.ForUserTurn, decisions: decisions);

        Assert.AreEqual(1, run.Executed.Count);
        var state = State(decisions.Requests.Single());
        Assert.AreEqual(MarkThemComplete, state["user_message"]);
        Assert.AreEqual("Install furnace filter and Order propane are overdue.", state["agent_message_before_it"]);
    }

    [TestMethod]
    public async Task UserTurn_ModelSaysNo_ToolIsRefused()
    {
        var run = await ConsequentialActionGateTests.RunWithToolAsync(false,
            ConsequentialActionGateTests.Conversation("the talk doesn't exist yet"), "calendar-mcp__create_event",
            actionGate: ActionGateScope.ForUserTurn, decisions: new FakeDecisionService(0.007));

        Assert.AreEqual(0, run.Executed.Count);
        StringAssert.Contains(run.ToolResult, "Not run:");
    }

    [TestMethod]
    public async Task SubagentWithoutParentRequest_ItsOwnTaskIsNeverJudgedAsTheUsersWords()
    {
        // A subagent's task description is imperative because the agent wrote it. Judging it as the
        // user's message would launder an information-only turn into an instruction.
        var decisions = new FakeDecisionService(0.99);

        var run = await ConsequentialActionGateTests.RunWithToolAsync(false,
            ConsequentialActionGateTests.Conversation("Create prep blocks for the Techorama talk."),
            "calendar-mcp__create_event",
            actionGate: new ActionGateScope(RunOrigin.SubagentOfUserTurn, UserRequestKind.InformationOnly),
            decisions: decisions);

        Assert.AreEqual(0, run.Executed.Count);
        Assert.AreEqual(0, decisions.Requests.Count);
    }
}

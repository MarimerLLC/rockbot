using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

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

    /// <summary>The body Microsoft-Decision-1 returned for this question on 2026-10-13.</summary>
    private const string ObservedResponse =
        """{"model":"microsoft-decision-1","answers":{"user_asked":{"noul":0.9890130586123322,"type":"noul"}},"usage":{"input_tokens":233,"output_tokens":1}}""";

    private static ActionGateScope InformationOnly(string userMessage = MarkThemComplete, string? previous = null) =>
        new(RunOrigin.UserTurn, UserRequestKind.InformationOnly) { Request = new ActionGateRequest(userMessage, previous) };

    private static ConsequentialActionGate Gate(FakeClassifier classifier, Action<DecisionModelOptions>? configure = null)
    {
        var host = new AgentHostOptions();
        configure?.Invoke(host.ConsequentialActionGate.DecisionModel);
        return new ConsequentialActionGate(Options.Create(host), intentClassifier: classifier);
    }

    // ── Gate decisions ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task ModelSaysUserAsked_CallRuns()
    {
        var classifier = new FakeClassifier(0.99);

        Assert.IsNull(await Gate(classifier).CheckAsync(CompleteTask, "id=1", InformationOnly()));
        var query = classifier.Queries.Single();
        Assert.AreEqual(MarkThemComplete, query.UserRequest);
        Assert.AreEqual(CompleteTask, query.ToolName);
        Assert.AreEqual("todo", query.Target);
    }

    [TestMethod]
    public async Task ModelSaysUserDidNotAsk_CallIsRefused()
    {
        var refusal = await Gate(new FakeClassifier(0.01)).CheckAsync(CompleteTask, "id=1", InformationOnly("why did it refuse?"));

        Assert.IsTrue(ConsequentialActions.IsRefusal(refusal), refusal);
    }

    [TestMethod]
    public async Task Threshold_IsConfigurable()
    {
        var scope = InformationOnly();

        Assert.IsNotNull(await Gate(new FakeClassifier(0.6), o => o.Threshold = 0.7).CheckAsync(CompleteTask, null, scope));
        Assert.IsNull(await Gate(new FakeClassifier(0.6), o => o.Threshold = 0.5).CheckAsync(CompleteTask, null, InformationOnly()));
    }

    [TestMethod]
    [DataRow(true, DisplayName = "allow on failure (default)")]
    [DataRow(false, DisplayName = "refuse on failure")]
    public async Task ModelFails_FollowsAllowOnFailure(bool allowOnFailure)
    {
        var result = await Gate(new FakeClassifier(null), o => o.AllowOnFailure = allowOnFailure)
            .CheckAsync(CompleteTask, null, InformationOnly());

        Assert.AreEqual(allowOnFailure, result is null);
    }

    [TestMethod]
    public async Task ModelNotConfigured_RegexAloneDecides()
    {
        var classifier = new FakeClassifier(0.99) { Enabled = false };

        Assert.IsNotNull(await Gate(classifier).CheckAsync(CompleteTask, null, InformationOnly()));
        Assert.AreEqual(0, classifier.Queries.Count);
    }

    [TestMethod]
    public async Task RegexAlreadyAllows_ModelNotAsked()
    {
        var classifier = new FakeClassifier(0.01);
        var instructed = new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.Instruction)
        {
            Request = new ActionGateRequest("mark them complete", null),
        };

        Assert.IsNull(await Gate(classifier).CheckAsync(CompleteTask, null, instructed));
        Assert.IsNull(await Gate(classifier).CheckAsync("todo__list_tasks", null, InformationOnly()), "reads are never gated");
        Assert.AreEqual(0, classifier.Queries.Count);
    }

    [TestMethod]
    public async Task NoUserRequest_ModelNotAsked()
    {
        var classifier = new FakeClassifier(0.99);

        Assert.IsNotNull(await Gate(classifier).CheckAsync(CompleteTask, null,
            new ActionGateScope(RunOrigin.UserTurn, UserRequestKind.InformationOnly)));
        Assert.AreEqual(0, classifier.Queries.Count);
    }

    // ── Caching and subagents ───────────────────────────────────────────────

    [TestMethod]
    public async Task OneQuestionPerToolPerTurn_SharedWithSubagents()
    {
        var classifier = new FakeClassifier(0.99);
        var gate = Gate(classifier);
        var turn = InformationOnly();
        var subagent = turn.ForSubagent();

        Assert.AreSame(turn.Request, subagent.Request, "a subagent judges its parent's user message");
        Assert.IsNull(await gate.CheckAsync(CompleteTask, "id=1", turn));
        Assert.IsNull(await gate.CheckAsync(CompleteTask, "id=2", subagent));
        Assert.AreEqual(1, classifier.Queries.Count, "the second completion reuses the first answer");

        await gate.CheckAsync("calendar-mcp__create_event", null, subagent);
        Assert.AreEqual(2, classifier.Queries.Count, "a different change is asked about separately");
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
        var classifier = new FakeClassifier(0.99);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "You are a test agent."),
            new(ChatRole.User, "what's overdue?"),
            new(ChatRole.Assistant, "Install furnace filter and Order propane are overdue."),
            new(ChatRole.User, MarkThemComplete),
        ];

        var run = await ConsequentialActionGateTests.RunWithToolAsync(textBased, messages, CompleteTask,
            actionGate: ActionGateScope.ForUserTurn, classifier: classifier);

        Assert.AreEqual(1, run.Executed.Count);
        var query = classifier.Queries.Single();
        Assert.AreEqual(MarkThemComplete, query.UserRequest);
        Assert.AreEqual("Install furnace filter and Order propane are overdue.", query.PreviousAgentMessage);
    }

    [TestMethod]
    public async Task UserTurn_ModelSaysNo_ToolIsRefused()
    {
        var run = await ConsequentialActionGateTests.RunWithToolAsync(false,
            ConsequentialActionGateTests.Conversation("the talk doesn't exist yet"), "calendar-mcp__create_event",
            actionGate: ActionGateScope.ForUserTurn, classifier: new FakeClassifier(0.007));

        Assert.AreEqual(0, run.Executed.Count);
        StringAssert.Contains(run.ToolResult, "Not run:");
    }

    [TestMethod]
    public async Task SubagentWithoutParentRequest_ItsOwnTaskIsNeverJudgedAsTheUsersWords()
    {
        // A subagent's task description is imperative because the agent wrote it. Judging it as the
        // user's message would launder an information-only turn into an instruction.
        var classifier = new FakeClassifier(0.99);

        var run = await ConsequentialActionGateTests.RunWithToolAsync(false,
            ConsequentialActionGateTests.Conversation("Create prep blocks for the Techorama talk."),
            "calendar-mcp__create_event",
            actionGate: new ActionGateScope(RunOrigin.SubagentOfUserTurn, UserRequestKind.InformationOnly),
            classifier: classifier);

        Assert.AreEqual(0, run.Executed.Count);
        Assert.AreEqual(0, classifier.Queries.Count);
    }

    // ── The Foundry client ──────────────────────────────────────────────────

    [TestMethod]
    public void ReadProbability_ParsesTheObservedResponse()
    {
        Assert.AreEqual(0.9890130586123322, FoundryDecisionIntentClassifier.ReadProbability(ObservedResponse)!.Value, 1e-12);
    }

    [TestMethod]
    [DataRow("""{"answers":{"user_asked":0.42}}""", 0.42)]
    [DataRow("""{"answers":{"user_asked":{"probability":0.3}}}""", 0.3)]
    [DataRow("""{"answers":{"user_asked":{"noul":1.7}}}""", 1.0)]
    public void ReadProbability_AcceptsOtherShapes(string body, double expected)
    {
        Assert.AreEqual(expected, FoundryDecisionIntentClassifier.ReadProbability(body)!.Value, 1e-12);
    }

    [TestMethod]
    [DataRow("""{"answers":{}}""")]
    [DataRow("""{"answers":{"user_asked":{"type":"noul"}}}""")]
    public void ReadProbability_NoAnswer_IsNull(string body)
    {
        Assert.IsNull(FoundryDecisionIntentClassifier.ReadProbability(body));
    }

    [TestMethod]
    public void RequestBody_IsOneNoulQuestionWithTheConversationAndCallAsState()
    {
        var body = FoundryDecisionIntentClassifier.BuildRequestBody("decision-1",
            new UserIntentQuery("mark them complete now", "Both are overdue.", CompleteTask, "todo", "id=1"));

        Assert.AreEqual("decision-1", (string?)body["model"]);
        Assert.AreEqual("mark them complete now", (string?)body["state"]!["user_message"]);
        Assert.AreEqual("Both are overdue.", (string?)body["state"]!["agent_message_before_it"]);
        Assert.AreEqual(CompleteTask, (string?)body["state"]!["proposed_action"]!["tool"]);
        Assert.AreEqual("noul", (string?)body["questions"]!["user_asked"]!["type"]);
        Assert.IsNotNull(body["questions"]!["user_asked"]!["criteria"]!["true"]);
    }

    [TestMethod]
    public async Task Client_PostsToSystemOneWithTheKey_AndReadsTheProbability()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ObservedResponse);
        var classifier = Client(handler);

        Assert.IsTrue(classifier.IsEnabled);
        var p = await classifier.ProbabilityUserAskedForAsync(Query(), CancellationToken.None);

        Assert.AreEqual(0.989, p!.Value, 0.001);
        Assert.AreEqual("https://acct.services.ai.azure.com/providers/microsoft/v1/systemone", handler.Uri!.ToString());
        Assert.AreEqual("test-key", handler.ApiKey);
        Assert.AreEqual(handler.Body!.Length, handler.ContentLength, "systemone needs Content-Length (HTTP 400 otherwise)");
        Assert.AreEqual("decision-1", (string?)JsonNode.Parse(handler.Body!)!["model"]);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task Client_HttpError_IsNull(HttpStatusCode status)
    {
        var p = await Client(new StubHandler(status, """{"error":{"code":"RateLimitReached"}}"""))
            .ProbabilityUserAskedForAsync(Query(), CancellationToken.None);

        Assert.IsNull(p);
    }

    [TestMethod]
    public async Task Client_Timeout_IsNull()
    {
        var p = await Client(new StubHandler(HttpStatusCode.OK, ObservedResponse, delay: TimeSpan.FromSeconds(5)),
                o => o.TimeoutSeconds = 0.5)
            .ProbabilityUserAskedForAsync(Query(), CancellationToken.None);

        Assert.IsNull(p);
    }

    [TestMethod]
    public void Options_BindFromTheHelmEnvKeys()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentHost:ConsequentialActionGate:DecisionModel:Endpoint"] = "https://acct.services.ai.azure.com",
                ["AgentHost:ConsequentialActionGate:DecisionModel:ApiKey"] = "k",
                ["AgentHost:ConsequentialActionGate:DecisionModel:Deployment"] = "decision-1",
                ["AgentHost:ConsequentialActionGate:DecisionModel:Threshold"] = "0.6",
                ["AgentHost:ConsequentialActionGate:DecisionModel:AllowOnFailure"] = "false",
            })
            .Build();
        var host = new AgentHostOptions();

        config.GetSection("AgentHost").Bind(host);

        var model = host.ConsequentialActionGate.DecisionModel;
        Assert.IsTrue(model.IsConfigured);
        Assert.AreEqual(0.6, model.Threshold);
        Assert.IsFalse(model.AllowOnFailure);
    }

    [TestMethod]
    public void Client_Unconfigured_IsDisabled()
    {
        Assert.IsFalse(new FoundryDecisionIntentClassifier(Options.Create(new AgentHostOptions())).IsEnabled);
    }

    private static UserIntentQuery Query() => new(MarkThemComplete, null, CompleteTask, "todo", "id=1");

    private static FoundryDecisionIntentClassifier Client(StubHandler handler, Action<DecisionModelOptions>? configure = null)
    {
        var host = new AgentHostOptions();
        var model = host.ConsequentialActionGate.DecisionModel;
        model.Endpoint = "https://acct.services.ai.azure.com/";
        model.ApiKey = "test-key";
        model.Deployment = "decision-1";
        configure?.Invoke(model);
        return new FoundryDecisionIntentClassifier(Options.Create(host), httpClient: new HttpClient(handler));
    }

    private sealed class FakeClassifier(double? probability) : IUserIntentClassifier
    {
        public bool Enabled { get; init; } = true;
        public List<UserIntentQuery> Queries { get; } = [];
        public bool IsEnabled => Enabled;

        public Task<double?> ProbabilityUserAskedForAsync(UserIntentQuery query, CancellationToken cancellationToken)
        {
            lock (Queries) Queries.Add(query);
            return Task.FromResult(probability);
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body, TimeSpan? delay = null) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? Body { get; private set; }
        public long? ContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("api-key", out var keys) ? keys.Single() : null;
            // Read before the body: Foundry rejects a request without Content-Length.
            ContentLength = request.Content?.Headers.ContentLength;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (delay is { } d) await Task.Delay(d, cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}

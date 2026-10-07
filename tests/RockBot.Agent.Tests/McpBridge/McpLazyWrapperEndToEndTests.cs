using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Tools;
using RockBot.Tools.Mcp;
using static RockBot.Agent.Tests.McpBridge.McpWrapperEndToEndTests;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// Lazy typed MCP tools end to end (#612): a scripted model drives the real agent loop
/// (<see cref="AgentLoopRunner"/> over <see cref="RockBotFunctionInvokingChatClient"/>, or the
/// text-based loop) against the real gateway — catalog, bridge, MCP server. A tool the model
/// finds, or names, in one iteration is offered and callable in the next, within the same turn.
/// </summary>
[TestClass]
public class McpLazyWrapperEndToEndTests
{
    private const string Session = "session/lazy-e2e";

    /// <summary>A model that follows a script, and records the tools each request offered.</summary>
    private sealed class ScriptedModel(params ChatMessage[] script) : IChatClient
    {
        private int _step;
        public List<string[]> ToolsOffered { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ToolsOffered.Add([.. options?.Tools?.Select(t => t.Name) ?? []]);
            var reply = _step < script.Length ? script[_step++] : new ChatMessage(ChatRole.Assistant, "(script ended)");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class LlmClient(IChatClient client) : ILlmClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct) =>
            client.GetResponseAsync(messages, options, ct);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options, CancellationToken ct) =>
            client.GetResponseAsync(messages, options, ct);
    }

    private static ChatMessage Call(string tool, Dictionary<string, object?> arguments) =>
        new(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, arguments)]);

    private static ChatMessage Text(string text) => new(ChatRole.Assistant, text);

    private static readonly Dictionary<string, object?> SendArgs = new() { ["to"] = new[] { "a@b.c" }, ["subject"] = "hi" };

    /// <summary>
    /// The loop the agent runs: native (function-invoking chat client) or text-based, each over the
    /// scripted model, with the agent's typed-tool surface.
    /// </summary>
    private static AgentLoopRunner CreateRunner(ScriptedModel model, bool textBased, ITypedToolSurface surface)
    {
        var behavior = new ModelBehavior { UseTextBasedToolCalling = textBased };
        var hostOptions = Options.Create(new AgentHostOptions());
        var workingMemory = new A2A.Tests.StubWorkingMemory();

        IChatClient client = textBased
            ? model
            : new RockBotFunctionInvokingChatClient(model, null, null, behavior,
                new LlmCostEstimator(
                    Options.Create(new LlmPricingOptions { ConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "p.json") }),
                    NullLogger<LlmCostEstimator>.Instance),
                workingMemory, hostOptions, NullLogger.Instance);

        var profileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), $"rockbot-test-{Guid.NewGuid():N}"),
        });

        return new AgentLoopRunner(
            new LlmClient(client),
            workingMemory,
            behavior,
            new A2A.Tests.StubFeedbackStore(),
            new AgentClock(new ConfigurationBuilder().Build(), profileOptions, NullLogger<AgentClock>.Instance),
            hostOptions,
            new StubSkillStore(),
            [],
            new StubConversationMemory(),
            NullLogger<AgentLoopRunner>.Instance,
            typedToolSurface: surface);
    }

    private static Task<string> RunTurnAsync(AgentLoopRunner runner, AgentSide agent, string session = Session) =>
        runner.RunAsync(
            [new ChatMessage(ChatRole.User, "Email a@b.c with the subject 'hi'.")],
            new ChatOptions { Tools = [.. agent.Registry.BuildAgentToolFunctions(session, "batch")] },
            session,
            enableFollowUp: false,
            enableCompletionEval: false,
            enableReasoningScaffolding: false);

    [TestMethod]
    [DataRow(false, DisplayName = "native")]
    [DataRow(true, DisplayName = "text-based")]
    public async Task FindThenTypedCall_InOneTurn_RunsTheDownstreamTool(bool textBased)
    {
        var executions = new Counter();
        await using var harness = await BridgeHarness.StartAsync([SendEmail(executions)]);
        var agent = await ConnectAgentAsync(harness, McpWrapperMode.Lazy);
        var model = new ScriptedModel(
            Call("mcp_find_tools", new() { ["query"] = "send email" }),
            Call("fixture__send_email", SendArgs),
            Text("Sent."));

        var reply = await RunTurnAsync(CreateRunner(model, textBased, agent.Surface), agent);

        Assert.AreEqual("Sent.", reply);
        Assert.AreEqual(1, executions.Value, "The typed call must reach the MCP server exactly once.");
        CollectionAssert.DoesNotContain(model.ToolsOffered[0], "fixture__send_email", "Lazy: not in the baseline.");
        CollectionAssert.Contains(model.ToolsOffered[1], "fixture__send_email",
            "The tool mcp_find_tools activated must be offered on the very next request.");
        Assert.AreEqual(0, agent.Surface.GetActivated("session/other").Count, "Other sessions don't see it.");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native")]
    [DataRow(true, DisplayName = "text-based")]
    public async Task CallByName_ToAnUnactivatedTypedTool_RunsItAndActivatesIt(bool textBased)
    {
        var executions = new Counter();
        await using var harness = await BridgeHarness.StartAsync([SendEmail(executions)]);
        var agent = await ConnectAgentAsync(harness, McpWrapperMode.Lazy);
        var model = new ScriptedModel(Call("fixture__send_email", SendArgs), Text("Sent."));

        var reply = await RunTurnAsync(CreateRunner(model, textBased, agent.Surface), agent);

        Assert.AreEqual("Sent.", reply);
        Assert.AreEqual(1, executions.Value, "A valid typed name runs even though it wasn't in the tool list.");
        Assert.IsTrue(agent.Surface.IsActivated(Session, "fixture__send_email"));
        CollectionAssert.Contains(model.ToolsOffered[1], "fixture__send_email");
    }

    [TestMethod]
    public async Task NextTurn_StartsWithTheSessionsActivations_AndOtherSessionsDont()
    {
        await using var harness = await BridgeHarness.StartAsync([SendEmail(new Counter())]);
        var agent = await ConnectAgentAsync(harness, McpWrapperMode.Lazy);
        await RunTurnAsync(CreateRunner(new ScriptedModel(
            Call("mcp_find_tools", new() { ["query"] = "send email" }), Text("Found it.")), false, agent.Surface), agent);

        var sameSession = new ScriptedModel(Text("ok"));
        await RunTurnAsync(CreateRunner(sameSession, false, agent.Surface), agent);
        var otherSession = new ScriptedModel(Text("ok"));
        await RunTurnAsync(CreateRunner(otherSession, false, agent.Surface), agent, "session/other");

        CollectionAssert.Contains(sameSession.ToolsOffered[0], "fixture__send_email");
        CollectionAssert.DoesNotContain(otherSession.ToolsOffered[0], "fixture__send_email");
    }

    [TestMethod]
    public async Task ServiceDetails_ActivatesTheServersTypedTools()
    {
        await using var harness = await BridgeHarness.StartAsync([SendEmail(new Counter()), DeleteEverything()]);
        var agent = await ConnectAgentAsync(harness, McpWrapperMode.Lazy);
        var details = agent.Registry.GetExecutor("mcp_get_service_details")!;

        var one = await details.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "1", ToolName = "mcp_get_service_details", SessionId = "s1",
            Arguments = """{"server_name":"fixture","tool_name":"send_email"}"""
        }, CancellationToken.None);
        var all = await details.ExecuteAsync(new ToolInvokeRequest
        {
            ToolCallId = "2", ToolName = "mcp_get_service_details", SessionId = "s2",
            Arguments = """{"server_name":"fixture"}"""
        }, CancellationToken.None);

        Assert.IsFalse(one.IsError, one.Content);
        StringAssert.Contains(one.Content, "Now callable in this conversation by typed name: fixture__send_email.");
        CollectionAssert.AreEqual(new[] { "fixture__send_email" }, agent.Surface.GetActivated("s1").Select(f => f.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "fixture__send_email", "fixture__delete_everything" },
            agent.Surface.GetActivated("s2").Select(f => f.Name).ToArray());
    }
}

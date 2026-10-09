using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Llm;

namespace RockBot.Agent.Tests;

/// <summary>
/// Mid-turn tier escalation end to end (#663): a scripted Low-tier model drives the real
/// agent loop — native (function-invoking chat client over the escalating client, wired as
/// <c>AddRockBotTieredChatClients</c> wires it) or text-based — and issues a
/// <c>file_write</c>. The loop's remaining LLM calls must go to the Balanced model.
/// </summary>
[TestClass]
public class TierEscalationEndToEndTests
{
    /// <summary>A model that follows a script and counts its calls.</summary>
    private sealed class ScriptedModel(string name, params ChatMessage[] script) : IChatClient
    {
        private int _step;
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var reply = _step < script.Length ? script[_step++] : new ChatMessage(ChatRole.Assistant, $"({name} script ended)");
            return Task.FromResult(new ChatResponse(reply) { ModelId = name });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Routes each request to the client registered for the requested tier.</summary>
    private sealed class TieredLlmClient(IChatClient low, IChatClient balanced) : ILlmClient
    {
        public List<ModelTier> TiersRequested { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct) =>
            GetResponseAsync(messages, ModelTier.Balanced, options, ct);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options, CancellationToken ct)
        {
            TiersRequested.Add(tier);
            return (tier == ModelTier.Low ? low : balanced).GetResponseAsync(messages, options, ct);
        }
    }

    private static ChatMessage Call(string tool, Dictionary<string, object?> arguments) =>
        new(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, arguments)]);

    private static ChatMessage Text(string text) => new(ChatRole.Assistant, text);

    private static (AgentLoopRunner Runner, TieredLlmClient Llm) CreateRunner(
        ScriptedModel lowModel, ScriptedModel balancedModel, bool textBased)
    {
        var behavior = new ModelBehavior { UseTextBasedToolCalling = textBased };
        var hostOptions = Options.Create(new AgentHostOptions());
        var workingMemory = new A2A.Tests.StubWorkingMemory();
        var costEstimator = new LlmCostEstimator(
            Options.Create(new LlmPricingOptions { ConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "p.json") }),
            NullLogger<LlmCostEstimator>.Instance);

        IChatClient lowClient, balancedClient;
        if (textBased)
        {
            lowClient = lowModel;
            balancedClient = balancedModel;
        }
        else
        {
            lowClient = new RockBotFunctionInvokingChatClient(
                new TierEscalatingChatClient(lowModel, balancedModel), null, null, behavior,
                costEstimator, workingMemory, hostOptions, NullLogger.Instance);
            balancedClient = new RockBotFunctionInvokingChatClient(
                balancedModel, null, null, behavior, costEstimator, workingMemory, hostOptions, NullLogger.Instance);
        }

        var llm = new TieredLlmClient(lowClient, balancedClient);
        var profileOptions = Options.Create(new AgentProfileOptions
        {
            BasePath = Path.Combine(Path.GetTempPath(), $"rockbot-test-{Guid.NewGuid():N}"),
        });

        var runner = new AgentLoopRunner(
            llm,
            workingMemory,
            behavior,
            new A2A.Tests.StubFeedbackStore(),
            new AgentClock(new ConfigurationBuilder().Build(), profileOptions, NullLogger<AgentClock>.Instance),
            hostOptions,
            new StubSkillStore(),
            [],
            new StubConversationMemory(),
            NullLogger<AgentLoopRunner>.Instance);
        return (runner, llm);
    }

    private static ChatOptions Tools(List<string> written, int failingFetches = 0)
    {
        var fetchFailuresLeft = failingFetches;
        return new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create((string path, string content) =>
                {
                    written.Add(path);
                    return "ok";
                }, "file_write"),
                AIFunctionFactory.Create((string path) => "file contents", "file_read"),
                AIFunctionFactory.Create((string url) =>
                    fetchFailuresLeft-- > 0 ? "Error: connection refused" : "page", "web_fetch"),
            ],
        };
    }

    private static Task<string> RunLowTurnAsync(
        AgentLoopRunner runner, ChatOptions options, LoopDiagnostics diag, bool allowTierEscalation = true) =>
        runner.RunAsync(
            [new ChatMessage(ChatRole.User, "figure out a way to update the doc")],
            options,
            "session/escalation-e2e",
            tier: ModelTier.Low,
            enableFollowUp: false,
            enableCompletionEval: false,
            enableReasoningScaffolding: false,
            diagnostics: diag,
            allowTierEscalation: allowTierEscalation);

    [TestMethod]
    [DataRow(false, DisplayName = "native")]
    [DataRow(true, DisplayName = "text-based")]
    public async Task LowTurn_FileWrite_EscalatesRemainingCallsToBalanced(bool textBased)
    {
        var low = new ScriptedModel("low",
            Call("file_write", new() { ["path"] = "deck.md", ["content"] = "# Deck" }),
            Text("low finished"));
        var balanced = new ScriptedModel("balanced", Text("Balanced finished the edit."));
        var (runner, llm) = CreateRunner(low, balanced, textBased);
        var written = new List<string>();
        var diag = new LoopDiagnostics();

        var reply = await RunLowTurnAsync(runner, Tools(written), diag);

        CollectionAssert.AreEqual(new[] { "deck.md" }, written, "The Low model's call still runs.");
        Assert.AreEqual(1, low.Calls, "After file_write, the Low model is not asked again.");
        Assert.AreEqual(1, balanced.Calls, "The rest of the loop runs on Balanced.");
        Assert.AreEqual("Balanced finished the edit.", reply);
        Assert.AreEqual(ModelTier.Balanced, diag.EscalatedTier);
        StringAssert.Contains(diag.EscalationReason, "file_write");
        Assert.AreEqual(ModelTier.Low, llm.TiersRequested[0], "The turn starts on its routed tier.");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native")]
    [DataRow(true, DisplayName = "text-based")]
    public async Task LowTurn_ReadOnlyCalls_StayLow(bool textBased)
    {
        var low = new ScriptedModel("low", Call("file_read", new() { ["path"] = "deck.md" }), Text("low finished"));
        var balanced = new ScriptedModel("balanced", Text("unexpected"));
        var (runner, _) = CreateRunner(low, balanced, textBased);
        var diag = new LoopDiagnostics();

        var reply = await RunLowTurnAsync(runner, Tools([]), diag);

        Assert.AreEqual("low finished", reply);
        Assert.AreEqual(2, low.Calls);
        Assert.AreEqual(0, balanced.Calls);
        Assert.IsNull(diag.EscalatedTier);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native")]
    [DataRow(true, DisplayName = "text-based")]
    public async Task LowTurn_TwoToolErrors_Escalate(bool textBased)
    {
        var low = new ScriptedModel("low",
            Call("web_fetch", new() { ["url"] = "https://a" }),
            Call("web_fetch", new() { ["url"] = "https://b" }),
            Text("low finished"));
        var balanced = new ScriptedModel("balanced", Text("Balanced recovered."));
        var (runner, _) = CreateRunner(low, balanced, textBased);
        var diag = new LoopDiagnostics();

        var reply = await RunLowTurnAsync(runner, Tools([], failingFetches: 2), diag);

        Assert.AreEqual("Balanced recovered.", reply);
        Assert.AreEqual(2, low.Calls);
        Assert.AreEqual(1, balanced.Calls);
        StringAssert.Contains(diag.EscalationReason, "tool errors");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native")]
    [DataRow(true, DisplayName = "text-based")]
    public async Task EscalationNotAllowed_StaysLow(bool textBased)
    {
        var low = new ScriptedModel("low",
            Call("file_write", new() { ["path"] = "deck.md", ["content"] = "# Deck" }),
            Text("low finished"));
        var balanced = new ScriptedModel("balanced", Text("unexpected"));
        var (runner, _) = CreateRunner(low, balanced, textBased);
        var diag = new LoopDiagnostics();

        var reply = await RunLowTurnAsync(runner, Tools([]), diag, allowTierEscalation: false);

        Assert.AreEqual("low finished", reply, "A pinned/opted-out run keeps its tier.");
        Assert.AreEqual(0, balanced.Calls);
        Assert.IsNull(diag.EscalatedTier);
    }
}

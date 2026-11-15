using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests;

/// <summary>
/// The agent describes its configuration to a user interface without its LLM (#834): what it says
/// must match what it runs, and nothing it says may carry a credential.
/// </summary>
[TestClass]
public sealed class AgentIntrospectionRequestHandlerTests
{
    private const string AnthropicKey = "sk-ant-api03-SECRET-anthropic";
    private const string AzureKey = "81fnpQeSECRETazure";
    private const string Foundry = "https://rocky-eastus2.cognitiveservices.azure.com/openai/v1/?api-key=" + AzureKey;

    private static readonly DateTimeOffset LoadedAt = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

    private string _pricingPath = null!;

    [TestInitialize]
    public void Init() =>
        _pricingPath = Path.Combine(Path.GetTempPath(), $"rb-introspect-pricing-{Guid.NewGuid():N}.json");

    [TestCleanup]
    public void Cleanup()
    {
        try { File.Delete(_pricingPath); } catch { /* best effort */ }
    }

    private static LlmConfigSnapshot Snapshot() => LlmConfigSnapshot.Create(
        new LlmTierOptions
        {
            Balanced = new() { Endpoint = Foundry, ApiKey = AzureKey, ModelId = "gpt-5.6-terra" },
            BalancedModels =
            [
                new() { Provider = "Anthropic", ApiKey = AnthropicKey, ModelId = "claude-haiku-5-5", SupportsImageInput = true },
                new() { Endpoint = Foundry, ApiKey = AzureKey, ModelId = "gpt-5.6-terra" },
            ],
            Low = new() { Endpoint = Foundry, ApiKey = AzureKey, ModelId = "gpt-5.6-luna" },
        },
        globalProvider: null,
        embedding: null,
        fixedTier: null,
        agentVersion: "0.18.4",
        llmConfigured: true,
        now: LoadedAt);

    private LlmCostEstimator Estimator() => new(
        Options.Create(new LlmPricingOptions { ConfigPath = _pricingPath }),
        NullLogger<LlmCostEstimator>.Instance);

    private static AgentIntrospectionResponse Build(
        AgentIntrospectionRequest request, LlmConfigSnapshot? snapshot, LlmCostEstimator? estimator) =>
        AgentIntrospectionRequestHandler.Build(request, "Rocky", "0.18.4", LoadedAt, snapshot, estimator);

    [TestMethod]
    public void NoSections_AnswersEverySection()
    {
        using var estimator = Estimator();

        var response = Build(new AgentIntrospectionRequest(), Snapshot(), estimator);

        Assert.AreEqual("Rocky", response.AgentName);
        Assert.AreEqual("0.18.4", response.AgentVersion);
        Assert.IsNotNull(response.Models);
        Assert.IsNotNull(response.Pricing);
    }

    [TestMethod]
    public void Sections_AnswersOnlyThoseAsked_IgnoringCaseAndUnknownNames()
    {
        using var estimator = Estimator();

        var response = Build(new AgentIntrospectionRequest { Sections = ["MODELS", "weather"] }, Snapshot(), estimator);

        Assert.IsNotNull(response.Models);
        Assert.IsNull(response.Pricing);
    }

    [TestMethod]
    public void Models_FollowTheSnapshot_WithEachModelsPrice()
    {
        using var estimator = Estimator();

        var models = Build(new AgentIntrospectionRequest(), Snapshot(), estimator).Models!;

        Assert.AreEqual(LoadedAt, models.LoadedAt);
        CollectionAssert.AreEqual(new[] { "Low", "Balanced", "High" }, models.Tiers.Select(t => t.Tier).ToArray());

        var balanced = models.Tiers.Single(t => t.Tier == "Balanced");
        CollectionAssert.AreEqual(new[] { "claude-haiku-5-5", "gpt-5.6-terra" }, balanced.Models.Select(m => m.ModelId).ToArray());
        Assert.AreEqual("Anthropic API", balanced.Models[0].Service);
        Assert.AreEqual("Azure AI Foundry", balanced.Models[1].Service);
        Assert.IsTrue(balanced.AcceptsImages);
        Assert.IsTrue(balanced.Models[0].SupportsImageInput);

        // The built-in table prices both; the row is the one cost is estimated with.
        Assert.AreEqual("claude-haiku-5-5", balanced.Models[0].Price!.Prefix);
        Assert.AreEqual(0.10, balanced.Models[0].Price!.InputPerM, 1e-9);
        Assert.AreEqual("gpt-5.6-terra", balanced.Models[1].Price!.Prefix);

        Assert.AreEqual("Balanced", models.Tiers.Single(t => t.Tier == "High").UsesTier);
        StringAssert.Contains(models.Summary, "Balanced: claude-haiku-5-5");
    }

    [TestMethod]
    public void Models_WithAModelNoRowPrices_HasNoPrice()
    {
        using var estimator = Estimator();
        var snapshot = LlmConfigSnapshot.Create(
            new LlmTierOptions { Balanced = new() { Provider = "Anthropic", ApiKey = AnthropicKey, ModelId = "house-model-7" } },
            null, null, null, "0.18.4", llmConfigured: true, LoadedAt);

        var model = Build(new AgentIntrospectionRequest(), snapshot, estimator).Models!.Tiers.Single(t => t.Tier == "Balanced").Models.Single();

        Assert.IsNull(model.Price);
    }

    [TestMethod]
    public void Pricing_SaysWhetherTheFileOrTheBuiltInTableIsInUse()
    {
        using (var builtIn = Estimator())
        {
            var pricing = Build(new AgentIntrospectionRequest(), null, builtIn).Pricing!;
            Assert.IsNull(pricing.LoadedFrom);
            Assert.IsTrue(pricing.Rows.Count > 10);
        }

        File.WriteAllText(_pricingPath, """
            [ { "prefix": "house-model", "inputPerM": 1.5, "outputPerM": 6, "cachedInputPerM": 0.15 },
              { "prefix": "house-model-7", "inputPerM": 2, "outputPerM": 8 } ]
            """);
        using var fromFile = Estimator();

        var loaded = Build(new AgentIntrospectionRequest(), null, fromFile).Pricing!;

        Assert.AreEqual(_pricingPath, loaded.LoadedFrom);
        CollectionAssert.AreEqual(new[] { "house-model-7", "house-model" }, loaded.Rows.Select(r => r.Prefix).ToArray(),
            "rows come longest prefix first, the order a model is matched in");
        Assert.AreEqual(0.15, loaded.Rows[1].CachedInputPerM!.Value, 1e-9);
    }

    [TestMethod]
    public void WithoutTheirSources_SectionsAreLeftOut()
    {
        var response = Build(new AgentIntrospectionRequest(), snapshot: null, estimator: null);

        Assert.IsNull(response.Models);
        Assert.IsNull(response.Pricing);
    }

    [TestMethod]
    public async Task HandleAsync_RepliesOnTheReplyTopic_WithNoCredentialInTheBody()
    {
        using var estimator = Estimator();
        var publisher = new RecordingPublisher();
        var handler = new AgentIntrospectionRequestHandler(publisher, new AgentNameHolder(),
            NullLogger<AgentIntrospectionRequestHandler>.Instance, Snapshot(), estimator);
        var request = new AgentIntrospectionRequest();

        await handler.HandleAsync(request, new MessageHandlerContext
        {
            Envelope = request.ToEnvelope<AgentIntrospectionRequest>(source: "proxy", correlationId: "c1", replyTo: "user.response.proxy"),
            Agent = new AgentIdentity("test-agent"),
            Services = new ServiceCollection().BuildServiceProvider(),
            CancellationToken = CancellationToken.None,
        });

        var (topic, envelope) = publisher.Published.Single();
        Assert.AreEqual("user.response.proxy", topic);
        Assert.AreEqual("c1", envelope.CorrelationId);
        Assert.AreEqual("test-agent", envelope.GetPayload<AgentIntrospectionResponse>()!.AgentName,
            "without a display name the agent's identity name is used");

        var body = Encoding.UTF8.GetString(envelope.Body.Span);
        Assert.IsFalse(body.Contains("SECRET", StringComparison.Ordinal), body);
        Assert.IsFalse(body.Contains("api-key", StringComparison.OrdinalIgnoreCase), body);
    }

    [TestMethod]
    public async Task HandleAsync_WithoutAReplyTopic_SendsNothing()
    {
        var publisher = new RecordingPublisher();
        var handler = new AgentIntrospectionRequestHandler(publisher, new AgentNameHolder(),
            NullLogger<AgentIntrospectionRequestHandler>.Instance);
        var request = new AgentIntrospectionRequest();

        await handler.HandleAsync(request, new MessageHandlerContext
        {
            Envelope = request.ToEnvelope<AgentIntrospectionRequest>(source: "proxy"),
            Agent = new AgentIdentity("test-agent"),
            Services = new ServiceCollection().BuildServiceProvider(),
            CancellationToken = CancellationToken.None,
        });

        Assert.AreEqual(0, publisher.Published.Count);
    }

    private sealed class RecordingPublisher : IMessagePublisher
    {
        public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = [];

        public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Published.Add((topic, envelope));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

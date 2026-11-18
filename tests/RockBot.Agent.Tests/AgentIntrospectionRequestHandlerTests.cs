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
    public async Task ScheduledTaskUsage_RunCostCoversThePatrolTurnAndItsSubagents_WithTheTaskCeiling()
    {
        // #846: a run's cost is everything charged to the task while it ran — the patrol turn (run
        // session patrol/{task}) and its subagents (thread patrol/{task}) — and nothing else.
        using var estimator = Estimator();
        var dir = Path.Combine(Path.GetTempPath(), $"rb-introspect-runs-{Guid.NewGuid():N}");
        try
        {
            var profile = Options.Create(new AgentProfileOptions { BasePath = dir });
            using var ledger = new LlmUsageLedger(estimator, Options.Create(new LlmUsageOptions()), profile,
                NullLogger<LlmUsageLedger>.Instance);
            var runCosts = new ScheduledTaskRunCosts(Options.Create(new ScheduledTaskCostOptions()), profile,
                NullLogger<ScheduledTaskRunCosts>.Instance, ledger);
            var usage = new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = 1_000_000, OutputTokenCount = 1_000_000 };

            ledger.Record("patrol/weekly", "scheduled", "claude-haiku-5-5", usage); // an earlier run
            runCosts.BeginRun("weekly", ModelTier.Balanced);
            ledger.Record("patrol/weekly", "scheduled", "claude-haiku-5-5", usage);
            ledger.Record("subagent-1", "patrol/weekly", "claude-haiku-5-5", usage);
            ledger.Record("session/blazor-session", "blazor-session", "claude-haiku-5-5", usage);
            runCosts.NoteTier("weekly", ModelTier.High);
            var check = await runCosts.EndRunAsync("weekly");

            Assert.AreEqual(1.80, ledger.ScheduledTaskCostToDate("weekly"), 1e-9);
            Assert.AreEqual(1.20, check!.CostUsd, 1e-9, "the turn and its subagent, at $0.60 each");

            var store = new InMemoryTaskStore(new ScheduledTask("weekly", "0 7 * * 1", "sweep", DateTimeOffset.UtcNow,
                MaxTier: ModelTier.Balanced, MaxTierReason: "High runs cost 5x"));
            var row = (await AgentIntrospectionRequestHandler.ScheduledTaskUsageAsync(runCosts, store)).Single();

            Assert.AreEqual("weekly", row.TaskName);
            Assert.AreEqual(1, row.Runs);
            Assert.AreEqual(1.20, row.LastCostUsd, 1e-9);
            Assert.AreEqual("High", row.LastRunTier);
            Assert.IsFalse(row.LastRunFlagged);
            Assert.AreEqual("Balanced", row.MaxTier);
            Assert.AreEqual("High runs cost 5x", row.MaxTierReason);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class InMemoryTaskStore(params ScheduledTask[] tasks) : IScheduledTaskStore
    {
        public Task SaveAsync(ScheduledTask task) => Task.CompletedTask;
        public Task<ScheduledTask?> GetAsync(string name) => Task.FromResult(tasks.FirstOrDefault(t => t.Name == name));
        public Task<IReadOnlyList<ScheduledTask>> ListAsync() => Task.FromResult<IReadOnlyList<ScheduledTask>>(tasks);
        public Task<bool> DeleteAsync(string name) => Task.FromResult(false);
        public Task UpdateLastFiredAsync(string name, DateTimeOffset firedAt) => Task.CompletedTask;
        public Task UpdateDirectiveAsync(string name, string directive) => Task.CompletedTask;
    }

    [TestMethod]
    public void Usage_ReportsTheLedger_WithTheAskingThreadsTotals()
    {
        using var estimator = Estimator();
        var dir = Path.Combine(Path.GetTempPath(), $"rb-introspect-usage-{Guid.NewGuid():N}");
        try
        {
            using var ledger = new LlmUsageLedger(estimator,
                Options.Create(new LlmUsageOptions()),
                Options.Create(new AgentProfileOptions { BasePath = dir }),
                NullLogger<LlmUsageLedger>.Instance);
            var usage = new Microsoft.Extensions.AI.UsageDetails { InputTokenCount = 1_000_000, OutputTokenCount = 1_000_000 };
            ledger.Record("session/blazor-session", "blazor-session", "claude-haiku-5-5", usage);
            ledger.Record("subagent-x", "blazor-session", "house-model-7", usage);
            ledger.Record("patrol/dream", null, "claude-haiku-5-5", usage);

            var report = AgentIntrospectionRequestHandler.Build(
                new AgentIntrospectionRequest { Sections = ["usage"], ThreadId = "blazor-session" },
                "Rocky", "0.18.4", LoadedAt, null, estimator, ledger).Usage!;

            Assert.AreEqual(3, report.TodayTotals.Requests);
            Assert.AreEqual(1.20, report.TodayTotals.CostUsd, 1e-9, "two priced requests at $0.60");
            Assert.AreEqual(1, report.TodayTotals.UnpricedRequests);
            Assert.AreEqual("blazor-session", report.Thread!.ThreadId);
            Assert.AreEqual(2, report.Thread.Totals.Requests);
            Assert.AreEqual("claude-haiku-5-5", report.Models[0].ModelId, "most expensive first");
            Assert.AreEqual(3, report.Kinds.Count);
            Assert.AreEqual(1, report.Days.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
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

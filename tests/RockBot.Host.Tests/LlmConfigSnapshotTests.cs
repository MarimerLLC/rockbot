using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// The snapshot behind the introspection sidecar's <c>get_llm_config</c> tool. Two things matter:
/// it must describe the clients the agent actually built, and no credential may reach the file.
/// </summary>
[TestClass]
public class LlmConfigSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 22, 0, 0, TimeSpan.Zero);

    private const string AnthropicKey = "sk-ant-api03-SECRET-anthropic";
    private const string OpenRouterKey = "sk-or-v1-SECRET-openrouter";
    private const string AzureKey = "81fnpQeSECRETazure";
    private const string Foundry = "https://rocky-ml1nznjr-eastus2.cognitiveservices.azure.com/openai/v1/";

    /// <summary>
    /// Today's live shape: two Anthropic-provider entries that differ only by endpoint, which is
    /// exactly the case where "provider" alone would mislead.
    /// </summary>
    private static LlmTierOptions LiveShape() => new()
    {
        Balanced = new() { Endpoint = Foundry, ApiKey = AzureKey, ModelId = "gpt-5.6-terra", SupportsImageInput = true },
        BalancedModels =
        [
            new() { Provider = "Anthropic", ApiKey = AnthropicKey, ModelId = "claude-haiku-5-5" },
            new() { Provider = "Anthropic", Endpoint = "https://openrouter.ai/api/v1", ApiKey = OpenRouterKey, ModelId = "anthropic/claude-haiku-5.5" },
            new() { Endpoint = Foundry, ApiKey = AzureKey, ModelId = "gpt-5.6-terra" }
        ],
        Low = new() { Endpoint = Foundry, ApiKey = AzureKey, ModelId = "gpt-5.6-luna", SupportsImageInput = true },
        High = new() { Endpoint = Foundry, ApiKey = AzureKey, ModelId = "gpt-5.6-sol", SupportsImageInput = true }
    };

    private static LlmConfigSnapshot Create(LlmTierOptions tiers, string? globalProvider = null,
        EmbeddingOptions? embedding = null, string? fixedTier = null, bool llmConfigured = true) =>
        LlmConfigSnapshot.Create(tiers, globalProvider, embedding, fixedTier, "0.18.0", llmConfigured, Now);

    [TestMethod]
    public void BalancedChainIsListedInPriorityOrderWithItsService()
    {
        var balanced = Create(LiveShape()).Tiers.Single(t => t.Tier == "Balanced");

        CollectionAssert.AreEqual(
            new[] { "claude-haiku-5-5", "anthropic/claude-haiku-5.5", "gpt-5.6-terra" },
            balanced.Models.Select(m => m.ModelId).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Anthropic API", "OpenRouter", "Azure AI Foundry" },
            balanced.Models.Select(m => m.Service).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Anthropic", "Anthropic", "OpenAI-compatible" },
            balanced.Models.Select(m => m.Provider).ToArray());
        CollectionAssert.AreEqual(
            new[] { "api.anthropic.com", "openrouter.ai", "rocky-ml1nznjr-eastus2.cognitiveservices.azure.com" },
            balanced.Models.Select(m => m.EndpointHost).ToArray());
    }

    [TestMethod]
    public void TiersWithTheirOwnModelDoNotClaimToUseBalanced()
    {
        var tiers = Create(LiveShape()).Tiers;

        var low = tiers.Single(t => t.Tier == "Low");
        var high = tiers.Single(t => t.Tier == "High");
        Assert.IsNull(low.UsesTier);
        Assert.AreEqual("gpt-5.6-luna", low.Models.Single().ModelId);
        Assert.IsTrue(low.AcceptsImages);
        Assert.IsNull(high.UsesTier);
        Assert.AreEqual("gpt-5.6-sol", high.Models.Single().ModelId);
    }

    [TestMethod]
    public void BalancedImageSupportComesFromTheFlagTheAgentReadsNotTheChainEntries()
    {
        // Live: the flag is set on the single Balanced entry only; no chain entry sets it.
        var balanced = Create(LiveShape()).Tiers.Single(t => t.Tier == "Balanced");
        Assert.IsTrue(balanced.AcceptsImages);

        var tiers = LiveShape();
        tiers.Balanced.SupportsImageInput = false;
        tiers.BalancedModels[0].SupportsImageInput = true;
        Assert.IsFalse(Create(tiers).Tiers.Single(t => t.Tier == "Balanced").AcceptsImages);
    }

    [TestMethod]
    public void AnUnconfiguredTierReportsTheBalancedModelItActuallyUses()
    {
        var tiers = LiveShape();
        tiers.Low = new();

        var low = Create(tiers).Tiers.Single(t => t.Tier == "Low");

        Assert.AreEqual("Balanced", low.UsesTier);
        // Resolve hands Low the single Balanced entry, not the chain, so that is what is reported.
        Assert.AreEqual("gpt-5.6-terra", low.Models.Single().ModelId);
    }

    [TestMethod]
    public void NoCredentialReachesTheSerializedSnapshot()
    {
        var tiers = LiveShape();
        // Key in the query string and in user-info: both must be dropped with the path.
        tiers.High.Endpoint = "https://user:SECRET-userinfo@my.openai.azure.com/openai/deployments/x?api-key=SECRET-query";
        var embedding = new EmbeddingOptions
        {
            Endpoint = "http://ollama.default.svc.cluster.local:11434/v1",
            Model = "nomic-embed-text",
            ApiKey = "SECRET-embedding"
        };

        var json = JsonSerializer.Serialize(Create(tiers, embedding: embedding));

        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("SECRET"));
        StringAssert.Contains(json, "my.openai.azure.com");
    }

    [TestMethod]
    public void CopilotIsReportedAsGitHubCopilotWithNoEndpoint()
    {
        var tiers = new LlmTierOptions { Balanced = new() { ModelId = "claude-sonnet-4.5" } };

        var model = Create(tiers, globalProvider: "copilot").Tiers.Single(t => t.Tier == "Balanced").Models.Single();

        Assert.AreEqual("Copilot", model.Provider);
        Assert.AreEqual("GitHub Copilot", model.Service);
        Assert.IsNull(model.EndpointHost);
    }

    [TestMethod]
    public void EmbeddingAndNonDefaultPortsAreDescribed()
    {
        var embedding = new EmbeddingOptions
        {
            Endpoint = "http://ollama.default.svc.cluster.local:11434/v1",
            Model = "nomic-embed-text"
        };

        var snapshot = Create(LiveShape(), embedding: embedding);

        Assert.AreEqual("nomic-embed-text", snapshot.Embedding!.ModelId);
        Assert.AreEqual("Ollama", snapshot.Embedding.Service);
        Assert.AreEqual("ollama.default.svc.cluster.local:11434", snapshot.Embedding.EndpointHost);
    }

    [TestMethod]
    public void WithNoLlmConfiguredTheSnapshotSaysSoInsteadOfInventingTiers()
    {
        var snapshot = Create(new LlmTierOptions(), llmConfigured: false);

        Assert.AreEqual(0, snapshot.Tiers.Count);
        StringAssert.Contains(snapshot.Describe(), "echo client");
    }

    [TestMethod]
    public void DescribeReadsAsOneLinePerTier()
    {
        var text = Create(LiveShape(), fixedTier: "Balanced").Describe();

        var lines = text.Split('\n');
        Assert.AreEqual(
            "Low: gpt-5.6-luna (Azure AI Foundry) [accepts images]", lines[0]);
        StringAssert.StartsWith(lines[1],
            "Balanced: claude-haiku-5-5 (Anthropic API), then anthropic/claude-haiku-5.5 (OpenRouter), then gpt-5.6-terra (Azure AI Foundry) [accepts images] [fallback chain");
        Assert.AreEqual("High: gpt-5.6-sol (Azure AI Foundry) [accepts images]", lines[2]);
        StringAssert.Contains(text, "pinned to the Balanced tier");
    }

    [TestMethod]
    public async Task WriterWritesAFileTheSidecarCanReadBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rockbot-llm-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new LlmConfigSnapshotWriter(
                Create(LiveShape()),
                Options.Create(new AgentProfileOptions { BasePath = dir }),
                NullLogger<LlmConfigSnapshotWriter>.Instance);

            await writer.StartAsync(CancellationToken.None);

            var path = Path.Combine(dir, LlmConfigFiles.SnapshotFile);
            Assert.AreEqual(path, writer.FilePath);
            Assert.IsFalse(File.Exists(path + ".tmp"));

            var roundTripped = JsonSerializer.Deserialize<LlmConfigSnapshot>(
                await File.ReadAllTextAsync(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.AreEqual(LlmConfigSnapshot.CurrentSchemaVersion, roundTripped.SchemaVersion);
            Assert.AreEqual(Now, roundTripped.WrittenAt);
            Assert.AreEqual("claude-haiku-5-5",
                roundTripped.Tiers.Single(t => t.Tier == "Balanced").Models[0].ModelId);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}

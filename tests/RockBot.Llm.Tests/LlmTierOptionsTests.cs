using Microsoft.VisualStudio.TestTools.UnitTesting;
using RockBot.Host;

namespace RockBot.Llm.Tests;

[TestClass]
public class LlmTierOptionsTests
{
    // ── IsConfigured ──────────────────────────────────────────────────────────

    [TestMethod]
    public void IsConfigured_AllFieldsSet_ReturnsTrue()
    {
        var config = new LlmTierConfig
        {
            Endpoint = "https://api.example.com",
            ApiKey   = "sk-test",
            ModelId  = "gpt-4o"
        };
        Assert.IsTrue(config.IsConfigured);
    }

    [TestMethod]
    [DataRow("", "sk-test", "gpt-4o")]
    [DataRow("https://api.example.com", "", "gpt-4o")]
    [DataRow("https://api.example.com", "sk-test", "")]
    [DataRow(null, "sk-test", "gpt-4o")]
    [DataRow("https://api.example.com", null, "gpt-4o")]
    [DataRow("https://api.example.com", "sk-test", null)]
    public void IsConfigured_MissingField_ReturnsFalse(string? endpoint, string? apiKey, string? modelId)
    {
        var config = new LlmTierConfig
        {
            Endpoint = endpoint,
            ApiKey   = apiKey,
            ModelId  = modelId
        };
        Assert.IsFalse(config.IsConfigured);
    }

    // ── Resolve fallback ──────────────────────────────────────────────────────

    [TestMethod]
    public void Resolve_LowNotConfigured_FallsBackToBalanced()
    {
        var opts = new LlmTierOptions
        {
            Balanced = new LlmTierConfig { Endpoint = "https://b.com", ApiKey = "b", ModelId = "b-model" },
            // Low intentionally empty
        };

        var resolved = opts.Resolve(ModelTier.Low);

        Assert.AreSame(opts.Balanced, resolved,
            "When Low is not configured, Resolve(Low) should return the Balanced config");
    }

    [TestMethod]
    public void Resolve_HighNotConfigured_FallsBackToBalanced()
    {
        var opts = new LlmTierOptions
        {
            Balanced = new LlmTierConfig { Endpoint = "https://b.com", ApiKey = "b", ModelId = "b-model" },
            // High intentionally empty
        };

        var resolved = opts.Resolve(ModelTier.High);

        Assert.AreSame(opts.Balanced, resolved,
            "When High is not configured, Resolve(High) should return the Balanced config");
    }

    [TestMethod]
    public void Resolve_LowConfigured_ReturnsLow()
    {
        var opts = new LlmTierOptions
        {
            Balanced = new LlmTierConfig { Endpoint = "https://b.com", ApiKey = "b", ModelId = "b-model" },
            Low      = new LlmTierConfig { Endpoint = "https://l.com", ApiKey = "l", ModelId = "l-model" },
        };

        var resolved = opts.Resolve(ModelTier.Low);

        Assert.AreSame(opts.Low, resolved,
            "When Low is fully configured, Resolve(Low) should return Low");
    }

    [TestMethod]
    public void Resolve_HighConfigured_ReturnsHigh()
    {
        var opts = new LlmTierOptions
        {
            Balanced = new LlmTierConfig { Endpoint = "https://b.com", ApiKey = "b", ModelId = "b-model" },
            High     = new LlmTierConfig { Endpoint = "https://h.com", ApiKey = "h", ModelId = "h-model" },
        };

        var resolved = opts.Resolve(ModelTier.High);

        Assert.AreSame(opts.High, resolved,
            "When High is fully configured, Resolve(High) should return High");
    }

    [TestMethod]
    public void Resolve_BalancedTier_AlwaysReturnsBalanced()
    {
        var opts = new LlmTierOptions
        {
            Balanced = new LlmTierConfig { Endpoint = "https://b.com", ApiKey = "b", ModelId = "b-model" },
            Low      = new LlmTierConfig { Endpoint = "https://l.com", ApiKey = "l", ModelId = "l-model" },
            High     = new LlmTierConfig { Endpoint = "https://h.com", ApiKey = "h", ModelId = "h-model" },
        };

        var resolved = opts.Resolve(ModelTier.Balanced);

        Assert.AreSame(opts.Balanced, resolved,
            "Resolve(Balanced) should always return Balanced regardless of other configs");
    }
}

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

    // ── Image support (#818) ─────────────────────────────────────────────────

    private static LlmTierConfig Entry(string model, bool sees = false) =>
        new() { Endpoint = "https://api.example.com", ApiKey = "k", ModelId = model, SupportsImageInput = sees };

    [TestMethod]
    public void ChainEntriesKeepTheirOwnImageFlags()
    {
        var opts = new LlmTierOptions
        {
            Balanced = Entry("single", sees: true),
            BalancedModels = [Entry("a", sees: true), Entry("b"), Entry("c", sees: true)]
        };

        CollectionAssert.AreEqual(new[] { true, false, true }, opts.BalancedChainImageSupport().ToArray());
        Assert.IsFalse(opts.UsesLegacyBalancedImageFlag);
    }

    [TestMethod]
    public void BalancedSeesWhenAnyChainEntrySeesEvenIfTheSingleEntryDoesNot()
    {
        var opts = new LlmTierOptions
        {
            Balanced = Entry("single"),
            BalancedModels = [Entry("a"), Entry("b", sees: true)]
        };

        Assert.IsTrue(opts.TierSupportsImageInput(ModelTier.Balanced));
    }

    [TestMethod]
    public void BalancedIsBlindWhenNoChainEntrySeesAndTheSingleFlagIsOff()
    {
        var opts = new LlmTierOptions
        {
            Balanced = Entry("single"),
            BalancedModels = [Entry("a"), Entry("b")]
        };

        Assert.IsFalse(opts.TierSupportsImageInput(ModelTier.Balanced));
    }

    [TestMethod]
    public void ALegacyConfigWithOnlyTheSingleFlagKeepsItsOldBehaviour()
    {
        // The live shape before #818: flag on the single Balanced entry, none on the chain.
        var opts = new LlmTierOptions
        {
            Balanced = Entry("single", sees: true),
            BalancedModels = [Entry("a"), Entry("b")]
        };

        Assert.IsTrue(opts.UsesLegacyBalancedImageFlag);
        CollectionAssert.AreEqual(new[] { true, true }, opts.BalancedChainImageSupport().ToArray());
        Assert.IsTrue(opts.TierSupportsImageInput(ModelTier.Balanced));
    }

    [TestMethod]
    public void ASeededSingleEntryIsNotMistakenForALegacyConfig()
    {
        // Program.cs seeds Balanced from BalancedModels[0] when no single entry is configured.
        var first = Entry("a", sees: true);
        var opts = new LlmTierOptions { Balanced = first, BalancedModels = [first, Entry("b")] };

        Assert.IsFalse(opts.UsesLegacyBalancedImageFlag);
        CollectionAssert.AreEqual(new[] { true, false }, opts.BalancedChainImageSupport().ToArray());
    }

    [TestMethod]
    public void LowAndHighStillUseTheirResolvedConfig()
    {
        var opts = new LlmTierOptions
        {
            Balanced = Entry("single", sees: true),
            BalancedModels = [Entry("a"), Entry("b")],
            High = Entry("high")
        };

        Assert.IsFalse(opts.TierSupportsImageInput(ModelTier.High));
        // Unconfigured Low resolves to the single Balanced entry, which is what its client is built from.
        Assert.IsTrue(opts.TierSupportsImageInput(ModelTier.Low));
    }

    [TestMethod]
    public void WithNoChainTheSingleFlagDecides()
    {
        var opts = new LlmTierOptions { Balanced = Entry("single", sees: true) };

        Assert.IsTrue(opts.TierSupportsImageInput(ModelTier.Balanced));
        Assert.AreEqual(0, opts.BalancedChainImageSupport().Count);
    }
}

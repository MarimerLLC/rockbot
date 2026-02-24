using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RockBot.Host;
using RockBot.Llm;

namespace RockBot.Llm.Tests;

[TestClass]
public class KeywordTierSelectorTests
{
    private readonly KeywordTierSelector _selector = new();

    // ── Low tier (score ≤ 0.28) ───────────────────────────────────────────────

    [TestMethod]
    [DataRow("What is the capital of France?")]
    [DataRow("Who was Abraham Lincoln?")]
    [DataRow("Define photosynthesis.")]
    [DataRow("Yes or no: is water wet?")]
    [DataRow("How many planets are in the solar system?")]
    public void SelectTier_SimpleFactualQuestion_ReturnsLow(string prompt)
    {
        var tier = _selector.SelectTier(prompt);
        Assert.AreEqual(ModelTier.Low, tier,
            $"Expected Low for prompt: \"{prompt}\"");
    }

    // ── Balanced tier (score 0.29 – 0.55) ────────────────────────────────────

    [TestMethod]
    [DataRow("Analyze the pros and cons of using a monolithic versus microservices approach for a small startup.")]
    [DataRow("Compare and contrast REST and GraphQL trade-offs for a large-scale distributed API design.")]
    [DataRow("Evaluate the security implications of using JWT tokens in a distributed web application.")]
    public void SelectTier_ModerateComplexityQuestion_ReturnsBalanced(string prompt)
    {
        var tier = _selector.SelectTier(prompt);
        Assert.AreEqual(ModelTier.Balanced, tier,
            $"Expected Balanced for prompt: \"{prompt}\"");
    }

    // ── High tier (score > 0.55) ──────────────────────────────────────────────

    [TestMethod]
    [DataRow("Design and architect a comprehensive distributed caching system for a high-traffic microservices platform. Analyze the trade-offs between consistency models including eventual consistency and strong consistency. Evaluate multiple approaches for cache invalidation, eviction policies, and partitions. Consider security implications and performance bottlenecks. Provide a thorough analysis with pros and cons for each recommended approach.")]
    [DataRow("Architect a microservices-based e-commerce platform with concurrent request handling and distributed coordination. Analyze the trade-offs between eventual consistency and strong consistency across multiple service boundaries. Evaluate multiple approaches for service discovery, load balancing, and fault tolerance. Provide a comprehensive recommendation with thorough pros and cons analysis, including security implications for the authentication layer.")]
    [DataRow("Perform a comprehensive analysis of distributed systems design trade-offs for a high-scale concurrent microservices architecture. Evaluate multiple consistency models and compare their performance bottlenecks. Design a security threat model and analyze the pros and cons of eventual versus strong consistency. Provide a thorough architectural recommendation that considers scalability and security implications across all service boundaries.")]
    public void SelectTier_HighComplexityQuestion_ReturnsHigh(string prompt)
    {
        var tier = _selector.SelectTier(prompt);
        Assert.AreEqual(ModelTier.High, tier,
            $"Expected High for prompt: \"{prompt}\"");
    }

    // ── Structural features ───────────────────────────────────────────────────

    [TestMethod]
    public void SelectTier_PromptWithCodeBlock_ScoresHigherThanWithout()
    {
        // A longer prompt with a code block should score higher than without
        var withCode = _selector.SelectTier(
            "Fix the bug in this function:\n```python\ndef foo(): pass\n```");
        var withoutCode = _selector.SelectTier("Fix the bug in this function.");

        // withCode should result in a higher or equal tier
        Assert.IsTrue((int)withCode >= (int)withoutCode,
            "Code block should push score upward");
    }

    [TestMethod]
    public void SelectTier_EmptyPrompt_ReturnsLow()
    {
        var tier = _selector.SelectTier(string.Empty);
        Assert.AreEqual(ModelTier.Low, tier,
            "Empty prompt should score minimally and return Low");
    }

    [TestMethod]
    public void SelectTier_VeryLongComplexPrompt_ReturnsHigh()
    {
        var prompt = string.Join(" ", Enumerable.Repeat(
            "analyze evaluate design architect comprehensive trade-off microservice distributed", 10));
        var tier = _selector.SelectTier(prompt);
        Assert.AreEqual(ModelTier.High, tier,
            "Long prompt full of complexity keywords should return High");
    }

    [TestMethod]
    public void SelectTier_SimplexKeywordsReduceScore()
    {
        // "what is" and "define" are simplex keywords that lower the score
        const string prompt = "What is the definition of REST?";
        var tier = _selector.SelectTier(prompt);

        // This prompt has "what is" and "definition of" as simplex signals
        // and few words — should remain Low
        Assert.AreEqual(ModelTier.Low, tier,
            "Simplex keywords should keep short simple prompts in the Low tier");
    }

    // ── Config-file hot-reload tests ──────────────────────────────────────────

    [TestMethod]
    public void SelectTier_WithConfigFile_HighBalancedCeiling_PushesHighToBalanced()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            // balancedCeiling = 0.99 means only a theoretically perfect-score prompt
            // can reach High; everything that previously scored High now scores Balanced.
            var configJson = """{"version":1,"balancedCeiling":0.99}""";
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), configJson);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // This prompt scores High with compiled defaults (verified by existing tests)
            const string prompt =
                "Design and architect a comprehensive distributed caching system for a high-traffic " +
                "microservices platform. Analyze the trade-offs between consistency models including " +
                "eventual consistency and strong consistency. Evaluate multiple approaches for cache " +
                "invalidation, eviction policies, and partitions. Consider security implications and " +
                "performance bottlenecks. Provide a thorough analysis with pros and cons for each " +
                "recommended approach.";

            var tier = selector.SelectTier(prompt);
            Assert.AreEqual(ModelTier.Balanced, tier,
                "balancedCeiling=0.99 should prevent any realistic prompt from reaching High");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void SelectTier_WithMissingConfigFile_BehavesLikeCompiledDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            // No tier-selector.json exists — DI ctor should fall back to compiled defaults
            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var configSelector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            string[] prompts =
            [
                "What is the capital of France?",
                "Define photosynthesis.",
                "Analyze the pros and cons of using a monolithic versus microservices approach for a small startup.",
            ];

            foreach (var prompt in prompts)
            {
                var expected = _selector.SelectTier(prompt);
                var actual   = configSelector.SelectTier(prompt);
                Assert.AreEqual(expected, actual,
                    $"Missing config file should produce same tier as compiled defaults for: \"{prompt}\"");
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}

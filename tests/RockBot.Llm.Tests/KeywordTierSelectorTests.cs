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

    // ── Classify() tests ─────────────────────────────────────────────────────

    [TestMethod]
    public void Classify_SimpleQuestion_ReturnsTierAndZeroKeywords()
    {
        var result = _selector.Classify("What is the capital of France?");

        Assert.AreEqual(ModelTier.Low, result.Tier);
        Assert.IsTrue(result.ComplexityScore <= 1.0,
            "Score must be ≤ 1.0");
        // "what is" is a low-signal keyword, so MatchedLowKeywords should be non-empty
        Assert.IsTrue(result.MatchedLowKeywords.Count > 0,
            "Simple question should match at least one low-signal keyword");
        Assert.AreEqual(0, result.MatchedHighKeywords.Count,
            "Simple factual question should not match high-signal keywords");
    }

    [TestMethod]
    public void Classify_ComplexPrompt_MatchesHighKeywords()
    {
        const string prompt =
            "Analyze the trade-offs between microservices and monolithic architectures " +
            "and design a comprehensive migration strategy.";

        var result = _selector.Classify(prompt);

        Assert.IsTrue(result.MatchedHighKeywords.Count > 0,
            "Complex prompt should match at least one high-signal keyword");
        Assert.IsTrue(result.Tier >= ModelTier.Balanced,
            "Complex prompt with high keywords should be Balanced or High");
    }

    [TestMethod]
    public void Classify_TierConsistentWithSelectTier()
    {
        string[] prompts =
        [
            "What is the capital of France?",
            "Define photosynthesis.",
            "Analyze the pros and cons of using microservices for a startup.",
            "Design a distributed caching system with comprehensive trade-off analysis.",
        ];

        foreach (var prompt in prompts)
        {
            var classification = _selector.Classify(prompt);
            var tier = _selector.SelectTier(prompt);
            Assert.AreEqual(tier, classification.Tier,
                $"Classify().Tier must match SelectTier() for: \"{prompt}\"");
        }
    }

    [TestMethod]
    public void Classify_ComplexityScoreMatchesKeywordPresence()
    {
        // A prompt with no high-signal keywords should score lower than one with several
        var simple = _selector.Classify("Tell me about dogs.");
        var complex = _selector.Classify("Analyze and evaluate the distributed microservices architecture trade-offs.");

        Assert.IsTrue(complex.ComplexityScore > simple.ComplexityScore,
            "High-keyword prompt should have a higher complexity score");
    }

    // ── Word-boundary matching tests ─────────────────────────────────────────

    [TestMethod]
    [DataRow("to", "tomorrow", false, DisplayName = "\"to\" must not match inside \"tomorrow\"")]
    [DataRow("to", "go to bed", true, DisplayName = "\"to\" matches as standalone word")]
    [DataRow("try", "country", false, DisplayName = "\"try\" must not match inside \"country\"")]
    [DataRow("try", "please try this", true, DisplayName = "\"try\" matches as standalone word")]
    [DataRow("rest", "restoration", false, DisplayName = "\"rest\" must not match inside \"restoration\"")]
    [DataRow("rest", "use rest api", true, DisplayName = "\"rest\" matches as standalone word")]
    [DataRow("add", "address", false, DisplayName = "\"add\" must not match inside \"address\"")]
    [DataRow("add", "add a feature", true, DisplayName = "\"add\" matches as standalone word")]
    [DataRow("is", "history", false, DisplayName = "\"is\" must not match inside \"history\"")]
    [DataRow("is", "is it ready", true, DisplayName = "\"is\" matches at start of text")]
    [DataRow("compare", "compare the options", true, DisplayName = "\"compare\" matches at word boundary")]
    [DataRow("trade off", "consider the trade off here", true, DisplayName = "multi-word phrase matches")]
    [DataRow("async", "use async tasks", true, DisplayName = "\"async\" matches as standalone word")]
    [DataRow("define", "define photosynthesis", true, DisplayName = "\"define\" matches without trailing space")]
    [DataRow("define", "predefined", false, DisplayName = "\"define\" must not match inside \"predefined\"")]
    public void ContainsWholePhrase_EnforcesWordBoundaries(
        string keyword, string text, bool expected)
    {
        var actual = KeywordTierSelector.ContainsWholePhrase(text, keyword);
        Assert.AreEqual(expected, actual,
            $"ContainsWholePhrase(\"{text}\", \"{keyword}\") should be {expected}");
    }

    [TestMethod]
    public void SelectTier_SubstringKeywordDoesNotInflateScore()
    {
        // "when is my flight tomorrow?" should not be inflated by substring matches
        // against short keywords like "to", "is", etc. Even if such keywords were
        // present in a runtime config, word-boundary matching prevents false hits.
        var result = _selector.Classify("when is my flight tomorrow?");

        // No high-signal keywords should match in this simple travel question
        Assert.AreEqual(0, result.MatchedHighKeywords.Count,
            "Simple travel question should not match any high-signal keywords");
    }

    // ── Config-file tests ─────────────────────────────────────────────────────

    [TestMethod]
    public void SelectTier_WithConfigFile_ShortKeywordsFiltered()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            // Config with short keywords that should be filtered out
            var configJson = """
                {
                    "version": 1,
                    "highSignalKeywords": ["to", "is", "analyze", "ok", "design", "hi"],
                    "lowSignalKeywords": ["what is", "a", "define"]
                }
                """;
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), configJson);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // "analyze" and "design" should survive filtering; "to", "is", "ok", "hi" should not
            var result = selector.Classify(
                "Analyze the design of this system.");

            Assert.IsTrue(result.MatchedHighKeywords.Contains("analyze"),
                "\"analyze\" (≥3 chars) should survive keyword filtering");
            Assert.IsTrue(result.MatchedHighKeywords.Contains("design"),
                "\"design\" (≥3 chars) should survive keyword filtering");
            Assert.IsFalse(result.MatchedHighKeywords.Contains("to"),
                "\"to\" (<3 chars) should be filtered out");
            Assert.IsFalse(result.MatchedHighKeywords.Contains("is"),
                "\"is\" (<3 chars) should be filtered out");
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

    // ── Topic blocklist ────────────────────────────────────────────────────────

    [TestMethod]
    public void TopicWords_InHighSignalKeywords_AreStripped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kts-blocklist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Config with topic words mixed in with real complexity signals
            var config = """
            {
                "highSignalKeywords": ["analyze", "calendar", "email", "architect", "todo", "mcp server", "design"],
                "lowSignalKeywords": ["hello", "thanks"]
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // "check my calendar" should NOT route High — "calendar" should be stripped
            var result = selector.Classify("check my calendar");
            Assert.AreNotEqual(ModelTier.High, result.Tier,
                "Topic word 'calendar' should be stripped from high-signal keywords");
            Assert.IsFalse(result.MatchedHighKeywords.Contains("calendar"),
                "Blocked topic word should not appear in matched keywords");

            // Real complexity keywords should survive and still affect scoring
            var complexResult = selector.Classify("analyze the architecture and design trade-offs");
            Assert.IsTrue(complexResult.MatchedHighKeywords.Contains("analyze"),
                "Complexity keyword 'analyze' should survive blocklist filtering");
            Assert.IsTrue(complexResult.MatchedHighKeywords.Contains("design"),
                "Complexity keyword 'design' should survive blocklist filtering");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void TopicWords_InLowSignalKeywords_AreNotStripped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kts-blocklist-low-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Topic words in lowSignalKeywords should NOT be stripped — blocklist only applies to high
            var config = """
            {
                "lowSignalKeywords": ["hello", "email", "calendar", "thanks"]
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // "email" in low-signal should still match and push score down
            var result = selector.Classify("email");
            Assert.IsTrue(result.MatchedLowKeywords.Contains("email"),
                "Topic words in low-signal list should not be blocked");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Origin bias tests ────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("what is the message from the minnesota dvs?")]
    [DataRow("I plan to get up at 9:30 am")]
    [DataRow("I think we should listen to some jazz tonight")]
    [DataRow("what do you think about the weather?")]
    public void Classify_UserOrigin_TrivialPrompts_RouteLow(string prompt)
    {
        var result = _selector.Classify(prompt, new TierRoutingContext(Origin: "user-message"));
        Assert.AreEqual(ModelTier.Low, result.Tier,
            $"Trivial user prompt should route Low: \"{prompt}\"");
    }

    [TestMethod]
    public void Classify_UserOriginBias_ReducesScore()
    {
        // Use a prompt that scores above 0.10 so the bias has room to reduce.
        // ~25 words, no keywords → lengthScore ~0.20, no keyword/structure contribution.
        const string prompt = "I went to the park yesterday and saw a few birds near the lake and it was a really nice afternoon walk overall";
        var withoutOrigin = _selector.Classify(prompt);
        var withOrigin = _selector.Classify(prompt, new TierRoutingContext(Origin: "user-message"));

        Assert.IsTrue(withoutOrigin.ComplexityScore > 0.10,
            $"Baseline score should be above 0.10 for this test to be meaningful, got {withoutOrigin.ComplexityScore:F3}");
        Assert.IsTrue(withOrigin.ComplexityScore < withoutOrigin.ComplexityScore,
            "User-origin bias should reduce the complexity score");
    }

    [TestMethod]
    public void Classify_SubagentOrigin_NoBiasApplied()
    {
        const string prompt = "Tell me about dogs.";
        var withoutOrigin = _selector.Classify(prompt);
        var withSubagent = _selector.Classify(prompt, new TierRoutingContext(Origin: "subagent"));

        Assert.AreEqual(withoutOrigin.ComplexityScore, withSubagent.ComplexityScore,
            "Subagent origin should not apply any score bias");
    }

    // ── Trivial guard tests ──────────────────────────────────────────────────

    [TestMethod]
    public void TrivialGuard_ForcesLow_EvenWhenLowCeilingIsTight()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kts-trivialguard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Set lowCeiling very tight (0.05) — without the trivial guard,
            // a prompt scoring 0.10 would route Balanced.
            var config = """{"version":1,"lowCeiling":0.05}""";
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // Short trivial prompt with no high keywords — trivial guard should force Low
            var result = selector.Classify("hello there");
            Assert.AreEqual(ModelTier.Low, result.Tier,
                "Trivial guard should force Low even when lowCeiling is very tight");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void TrivialGuard_DoesNotForce_WhenHighKeywordsPresent()
    {
        // With a tight lowCeiling, a short prompt with a high keyword should route
        // Balanced (not be pulled back to Low by the trivial guard).
        var tempDir = Path.Combine(Path.GetTempPath(), "kts-guard-high-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var config = """{"version":1,"lowCeiling":0.05}""";
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // "analyze this" scores ~0.15 (length 0.05 + keyword 0.10).
            // With lowCeiling=0.05, it routes Balanced. The trivial guard should NOT
            // force it to Low because "analyze" is a matched high keyword.
            var result = selector.Classify("analyze this");
            Assert.AreEqual(ModelTier.Balanced, result.Tier,
                "Trivial guard should not force Low when high-signal keywords are present");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Conversational keyword tests ─────────────────────────────────────────

    [TestMethod]
    [DataRow("I think we should go hiking tomorrow")]
    [DataRow("I plan to read a book tonight")]
    [DataRow("What do you think about that?")]
    [DataRow("Good evening, how are you?")]
    [DataRow("Sounds good, thanks!")]
    public void Classify_ConversationalPatterns_MatchLowKeywords(string prompt)
    {
        var result = _selector.Classify(prompt);
        Assert.IsTrue(result.MatchedLowKeywords.Count > 0,
            $"Conversational prompt should match low-signal keywords: \"{prompt}\"");
    }

    [TestMethod]
    public void Classify_HighTier_NotAffectedByOriginBias()
    {
        // Complex prompts should stay High even with user-origin bias
        const string prompt =
            "Design and architect a comprehensive distributed caching system for a high-traffic " +
            "microservices platform. Analyze the trade-offs between consistency models including " +
            "eventual consistency and strong consistency. Evaluate multiple approaches for cache " +
            "invalidation, eviction policies, and partitions. Consider security implications and " +
            "performance bottlenecks. Provide a thorough analysis with pros and cons for each " +
            "recommended approach.";

        var result = _selector.Classify(prompt, new TierRoutingContext(Origin: "user-message"));
        Assert.AreEqual(ModelTier.High, result.Tier,
            "Complex prompts should remain High even with user-origin bias");
    }
}

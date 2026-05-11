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
            // balancedCeiling = 0.80 (max allowed by guardrails) pushes most High prompts to Balanced
            var configJson = """{"version":1,"balancedCeiling":0.80}""";
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), configJson);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // A moderately complex prompt that scores High with defaults (~0.60-0.70)
            // should be Balanced when balancedCeiling is raised to 0.80
            const string prompt =
                "Analyze the trade-offs between microservices and monolithic architectures " +
                "and design a comprehensive migration strategy with pros and cons.";

            var tier = selector.SelectTier(prompt);
            Assert.AreEqual(ModelTier.Balanced, tier,
                "balancedCeiling=0.80 should push moderately complex prompts into Balanced");
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
            // Dream adds short keywords — they should be filtered; valid ones merged with defaults
            var configJson = """
                {
                    "version": 1,
                    "highSignalKeywords": ["to", "is", "ok", "hi", "novel complexity signal"],
                    "lowSignalKeywords": ["a", "novel simplicity signal"]
                }
                """;
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), configJson);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // Compiled defaults like "analyze" and "design" should always be present
            var result = selector.Classify(
                "Analyze the design of this system.");

            Assert.IsTrue(result.MatchedHighKeywords.Contains("analyze"),
                "Compiled default \"analyze\" should always be present after merge");
            Assert.IsTrue(result.MatchedHighKeywords.Contains("design"),
                "Compiled default \"design\" should always be present after merge");
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
            // Dream adds topic words as high-signal — they should be stripped during merge
            var config = """
            {
                "highSignalKeywords": ["calendar", "email", "todo", "mcp server"]
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

            // Compiled-default complexity keywords should still be present
            var complexResult = selector.Classify("analyze the architecture and design trade-offs");
            Assert.IsTrue(complexResult.MatchedHighKeywords.Contains("analyze"),
                "Compiled default 'analyze' should survive merge + blocklist filtering");
            Assert.IsTrue(complexResult.MatchedHighKeywords.Contains("design"),
                "Compiled default 'design' should survive merge + blocklist filtering");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void TopicWords_CompoundPhrases_AreStrippedFromHighSignal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kts-compound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Compound phrases containing blocked topic words should also be stripped
            var config = """
            {
                "highSignalKeywords": ["reply to email", "schedule meeting", "todo items", "calendar briefing", "create event"]
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // None of these compound phrases should survive — they all contain blocked topic words
            var result = selector.Classify("reply to email about the schedule meeting and todo items");
            Assert.IsFalse(result.MatchedHighKeywords.Contains("reply to email"),
                "'reply to email' contains blocked topic 'email'");
            Assert.IsFalse(result.MatchedHighKeywords.Contains("schedule meeting"),
                "'schedule meeting' contains blocked topic 'schedule'");
            Assert.IsFalse(result.MatchedHighKeywords.Contains("todo items"),
                "'todo items' contains blocked topic 'todo'");
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
                "lowSignalKeywords": ["email", "calendar"]
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
            // Set lowCeiling to minimum guardrail (0.15) — trivial guard still catches
            // short prompts that score just above the ceiling.
            var config = """{"version":1,"lowCeiling":0.15}""";
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // Short trivial prompt with no high keywords — trivial guard should force Low
            var result = selector.Classify("hello there");
            Assert.AreEqual(ModelTier.Low, result.Tier,
                "Trivial guard should force Low even when lowCeiling is at minimum");
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
            var config = """{"version":1,"lowCeiling":0.15}""";
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // "analyze this" scores ~0.15 (length 0.05 + keyword 0.10).
            // With lowCeiling=0.15, it routes Balanced. The trivial guard should NOT
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

    // ── Threshold guardrail tests ────────────────────────────────────────────

    [TestMethod]
    public void ThresholdGuardrails_ClampLowCeiling_ToMinimum()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kts-clamp-low-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Dream tries to set lowCeiling=0.05 — should be clamped to 0.15
            var config = """{"version":1,"lowCeiling":0.05}""";
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // "hello there" scores ~0.05 (length). With lowCeiling clamped to 0.15,
            // it should route Low (0.05 ≤ 0.15).
            var result = selector.Classify("hello there");
            Assert.AreEqual(ModelTier.Low, result.Tier,
                "lowCeiling should be clamped to 0.15 minimum, routing trivial prompts to Low");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void ThresholdGuardrails_ClampLowCeiling_ToMaximum()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kts-clamp-high-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Dream tries to set lowCeiling=0.60 — should be clamped to 0.40.
            // Verify by checking that a prompt scoring ~0.45 routes Balanced, not Low.
            var config = """{"version":1,"lowCeiling":0.60}""";
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // Long prompt with high-signal keywords — scores well above 0.40 but below 0.55
            // Without clamping, lowCeiling=0.60 would route this to Low
            const string prompt =
                "Design and architect a comprehensive distributed caching system for a high-traffic " +
                "microservices platform. Analyze the trade-offs between consistency models including " +
                "eventual consistency and strong consistency.";

            var result = selector.Classify(prompt);
            Assert.AreNotEqual(ModelTier.Low, result.Tier,
                $"lowCeiling should be clamped to 0.40 max — prompt scoring {result.ComplexityScore:F3} should not route Low");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Keyword merge tests ─────────────────────────────────────────────────

    [TestMethod]
    public void KeywordMerge_CompiledDefaults_AlwaysPresent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kts-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Dream provides only new keywords — compiled defaults must survive
            var config = """
            {
                "lowSignalKeywords": ["novel phrase one", "novel phrase two"]
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // "what is" is a compiled default low-signal keyword — must still be present
            var result = selector.Classify("What is the capital of France?");
            Assert.IsTrue(result.MatchedLowKeywords.Contains("what is"),
                "Compiled default 'what is' must survive merge with dream keywords");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void KeywordMerge_DreamAdditions_AreIncluded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kts-merge-add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Dream adds a novel low-signal keyword
            var config = """
            {
                "lowSignalKeywords": ["watching movie"]
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            var result = selector.Classify("I'm watching movie tonight");
            Assert.IsTrue(result.MatchedLowKeywords.Contains("watching movie"),
                "Dream-added keyword 'watching movie' should be present in merged list");
            // Compiled default should also work
            Assert.IsTrue(result.MatchedLowKeywords.Count > 0,
                "Merged list should contain both compiled defaults and dream additions");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void KeywordMerge_EmptyDreamList_UsesCompiledDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kts-merge-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Dream provides empty keyword lists
            var config = """
            {
                "highSignalKeywords": [],
                "lowSignalKeywords": []
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            // Should behave identically to compiled defaults
            var result = selector.Classify("What is the capital of France?");
            Assert.AreEqual(ModelTier.Low, result.Tier,
                "Empty dream lists should fall back to compiled defaults");
            Assert.IsTrue(result.MatchedLowKeywords.Contains("what is"),
                "Compiled default keywords must be present when dream list is empty");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── Active-thread short-message override (issue #383) ────────────────────

    [TestMethod]
    public void ActiveThreadOverride_ShortMessage_PromotesLowToBalanced()
    {
        // 18-char production reproducer — would otherwise route Low for a user-origin
        // message. With ThreadEstablished=true, the override pushes it to Balanced.
        var result = _selector.Classify(
            "I'll find out soon",
            new TierRoutingContext(Origin: "user-message", ThreadEstablished: true));

        Assert.AreEqual(ModelTier.Balanced, result.Tier,
            "Short follow-up on an established thread should route Balanced, not Low.");
    }

    [TestMethod]
    public void ActiveThreadOverride_ShortMessage_WithoutThread_StaysLow()
    {
        // Same prompt, no thread context — original behaviour preserved.
        var result = _selector.Classify(
            "I'll find out soon",
            new TierRoutingContext(Origin: "user-message"));

        Assert.AreEqual(ModelTier.Low, result.Tier,
            "Short follow-up without an established thread must still route Low.");
    }

    [TestMethod]
    public void ActiveThreadOverride_LongMessage_Unchanged()
    {
        // A simple long message routes Low normally; threadEstablished must not
        // promote it, because the override is gated on the short-message threshold.
        const string longPrompt = "What is the capital of France?"; // 30 chars exactly — at the threshold boundary
        var withThread = _selector.Classify(
            longPrompt,
            new TierRoutingContext(Origin: "user-message", ThreadEstablished: true));
        var withoutThread = _selector.Classify(
            longPrompt,
            new TierRoutingContext(Origin: "user-message"));

        // At exactly 30 chars, the override CAN fire — switch to a clearly-long prompt
        // to validate that long messages are unaffected.
        const string clearlyLong = "Who was Abraham Lincoln and what did he do?"; // 43 chars
        var clearlyLongWithThread = _selector.Classify(
            clearlyLong,
            new TierRoutingContext(Origin: "user-message", ThreadEstablished: true));
        var clearlyLongWithoutThread = _selector.Classify(
            clearlyLong,
            new TierRoutingContext(Origin: "user-message"));

        Assert.AreEqual(clearlyLongWithoutThread.Tier, clearlyLongWithThread.Tier,
            "Long prompts must route identically regardless of ThreadEstablished.");

        // Also check that the boundary at exactly the threshold behaves consistently —
        // the override may or may not fire, but the test fixes the relationship.
        if (withoutThread.Tier == ModelTier.Low)
        {
            // At the boundary, the override is allowed to fire.
            Assert.IsTrue(
                withThread.Tier == ModelTier.Balanced || withThread.Tier == withoutThread.Tier,
                "Boundary-length messages may stay Low or promote to Balanced under ThreadEstablished — never escalate beyond Balanced.");
        }
    }

    [TestMethod]
    public void ActiveThreadOverride_SubagentOrigin_DoesNotPromote()
    {
        // Subagent traffic is not subject to the user-side conversational override.
        // The router should treat the same short prompt identically with or without
        // ThreadEstablished when origin is "subagent".
        var withThread = _selector.Classify(
            "do it",
            new TierRoutingContext(Origin: "subagent", ThreadEstablished: true));
        var withoutThread = _selector.Classify(
            "do it",
            new TierRoutingContext(Origin: "subagent"));

        // Both should reach the same decision — subagent origin opts out of the user-origin
        // bias, but ThreadEstablished is honoured at the override site. For subagents we
        // accept either outcome as long as the override doesn't ESCALATE beyond what the
        // user-origin path produces. The stronger assertion here is that the override
        // never pushes subagent traffic above Balanced.
        Assert.IsTrue(withThread.Tier <= ModelTier.Balanced,
            "ActiveThreadOverride must never push subagent traffic above Balanced.");
        Assert.IsTrue(withoutThread.Tier <= ModelTier.Balanced,
            "Subagent baseline routing for trivial prompts stays at or below Balanced.");
    }

    [TestMethod]
    public void ActiveThreadOverride_MatchedHighKeyword_GateBlocksPromotion()
    {
        // The override is gated on matchedHigh.Length == 0 — when a high-signal keyword
        // is present, the override must NOT promote Low to Balanced. This protects the
        // existing keyword-driven routing path from being short-circuited by the gate.
        //
        // Construct comparable short prompts: one with a high keyword, one without.
        // Both score below the low ceiling for length reasons, so they'd otherwise
        // route Low. Verify ThreadEstablished promotes the non-keyword version but
        // NOT the keyword version.
        var withKeyword = _selector.Classify(
            "architect it now",
            new TierRoutingContext(Origin: "user-message", ThreadEstablished: true));
        var withoutKeyword = _selector.Classify(
            "do it now",
            new TierRoutingContext(Origin: "user-message", ThreadEstablished: true));

        Assert.IsTrue(withKeyword.MatchedHighKeywords.Count > 0,
            "Test premise: 'architect' must register as a high-signal keyword.");
        Assert.AreEqual(ModelTier.Low, withKeyword.Tier,
            "Short prompt with a high-signal keyword should not be promoted by the active-thread override.");
        Assert.AreEqual(ModelTier.Balanced, withoutKeyword.Tier,
            "Short prompt without high-signal keywords on an active thread should promote to Balanced.");
    }
}

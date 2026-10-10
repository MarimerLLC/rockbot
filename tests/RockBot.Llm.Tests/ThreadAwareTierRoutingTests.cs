using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RockBot.Host;
using RockBot.Llm;

namespace RockBot.Llm.Tests;

/// <summary>
/// Thread-aware tier routing (#663): the active-thread floor gated on message shape rather
/// than length, inherited tiers, the active-subagent floor, the research-question floor, the
/// trivial-acknowledgement exemption, retired low signals, and the rule each decision reports.
/// </summary>
[TestClass]
public class ThreadAwareTierRoutingTests
{
    private readonly KeywordTierSelector _selector = new();

    private static TierRoutingContext User(
        bool threadEstablished = false, ModelTier? recentMaxTier = null, bool activeSubagent = false) =>
        new(Origin: "user-message", ThreadEstablished: threadEstablished,
            RecentMaxTier: recentMaxTier, ActiveSubagent: activeSubagent);

    // ── Active-thread floor ──────────────────────────────────────────────────

    [TestMethod]
    public void EstablishedThread_34CharFollowUp_RoutesBalanced()
    {
        // Production reproducer: 34 chars, just over the old 30-char gate, so it went Low —
        // and that Low turn rewrote a file from a partial read.
        const string prompt = "figure out a way to update the doc";
        Assert.AreEqual(34, prompt.Length, "Test premise: the production message length.");

        var result = _selector.Classify(prompt, User(threadEstablished: true));

        Assert.AreEqual(ModelTier.Balanced, result.Tier);
        Assert.AreEqual(TierRoutingRules.ActiveThreadFloor, result.Rule);
    }

    [TestMethod]
    public void EstablishedThread_34CharFollowUp_WithRecentBalancedTurn_RoutesBalanced()
    {
        // Acceptance test from the issue: established thread + a recent Balanced turn.
        var result = _selector.Classify("figure out a way to update the doc",
            User(threadEstablished: true, recentMaxTier: ModelTier.Balanced));

        Assert.AreEqual(ModelTier.Balanced, result.Tier);
    }

    [TestMethod]
    [DataRow("ok, create a deck for me to review")]
    [DataRow("I see the outline - 30 slides is a lot - I typically estimate 5-6 minutes per slide")]
    [DataRow("so do it now")]
    [DataRow("do that")]
    [DataRow("why didn't you do a web search to find the details?")]
    [DataRow("yes")]
    [DataRow("go ahead")]
    public void EstablishedThread_WorkInstructions_NeverRouteLow(string prompt)
    {
        var result = _selector.Classify(prompt, User(threadEstablished: true));

        Assert.AreNotEqual(ModelTier.Low, result.Tier,
            $"\"{prompt}\" continues the thread's work and must not route Low (rule={result.Rule}).");
    }

    [TestMethod]
    [DataRow("ok thanks")]
    [DataRow("thanks!")]
    [DataRow("got it")]
    [DataRow("ok")]
    [DataRow("👍")]
    [DataRow("Thanks so much, that's great")]
    [DataRow("good morning")]
    public void EstablishedThread_TrivialAck_MayStayLow(string prompt)
    {
        var result = _selector.Classify(prompt, User(threadEstablished: true));

        Assert.AreEqual(ModelTier.Low, result.Tier, $"\"{prompt}\" is a pure acknowledgement.");
        Assert.AreEqual(TierRoutingRules.TrivialAck, result.Rule,
            "The log must say the thread floor was skipped because the message is a trivial ack.");
    }

    [TestMethod]
    public void NoThread_ShortFollowUp_StaysOnScoreBand()
    {
        var result = _selector.Classify("figure out a way to update the doc", User());

        Assert.AreEqual(ModelTier.Low, result.Tier);
        Assert.AreEqual(TierRoutingRules.ScoreBand, result.Rule);
    }

    // ── Inherited tier ───────────────────────────────────────────────────────

    [TestMethod]
    public void RecentHighTurn_FollowUpInheritsHigh()
    {
        var result = _selector.Classify("do that",
            User(threadEstablished: true, recentMaxTier: ModelTier.High));

        Assert.AreEqual(ModelTier.High, result.Tier);
        Assert.AreEqual(TierRoutingRules.InheritedTier, result.Rule);
        Assert.AreEqual(ModelTier.Balanced, result.IntrinsicTier,
            "The intrinsic tier (recorded in history) excludes inheritance so it decays.");
    }

    [TestMethod]
    public void RecentHighTurn_TrivialAck_DoesNotInherit()
    {
        var result = _selector.Classify("thanks!",
            User(threadEstablished: true, recentMaxTier: ModelTier.High));

        Assert.AreEqual(ModelTier.Low, result.Tier);
        Assert.AreEqual(TierRoutingRules.TrivialAck, result.Rule);
    }

    [TestMethod]
    public void RecentLowTurns_DoNotLowerAHigherIntrinsicTier()
    {
        const string prompt =
            "Design and architect a comprehensive distributed caching system for a high-traffic " +
            "microservices platform. Analyze the trade-offs between consistency models including " +
            "eventual consistency and strong consistency. Evaluate multiple approaches for cache " +
            "invalidation, eviction policies, and partitions. Consider security implications and " +
            "performance bottlenecks. Provide a thorough analysis with pros and cons for each " +
            "recommended approach.";

        var result = _selector.Classify(prompt, User(threadEstablished: true, recentMaxTier: ModelTier.Low));

        Assert.AreEqual(ModelTier.High, result.Tier, "Inheritance only ever lifts a tier.");
        Assert.AreEqual(TierRoutingRules.ScoreBand, result.Rule);
    }

    [TestMethod]
    public void SubagentOrigin_IgnoresThreadState()
    {
        var result = _selector.Classify("do that",
            new TierRoutingContext(Origin: "subagent", ThreadEstablished: true,
                RecentMaxTier: ModelTier.High, ActiveSubagent: true));

        Assert.AreEqual(ModelTier.Low, result.Tier, "Thread-state floors apply to user messages only.");
    }

    // ── Active-subagent floor ────────────────────────────────────────────────

    [TestMethod]
    public void ActiveSubagent_YoungThread_FloorsAtBalanced()
    {
        var result = _selector.Classify("is it finished", User(activeSubagent: true));

        Assert.AreEqual(ModelTier.Balanced, result.Tier);
        Assert.AreEqual(TierRoutingRules.ActiveSubagentFloor, result.Rule);
    }

    [TestMethod]
    public void ActiveSubagent_TrivialAck_StaysLow()
    {
        var result = _selector.Classify("ok thanks", User(activeSubagent: true));

        Assert.AreEqual(ModelTier.Low, result.Tier);
        Assert.AreEqual(TierRoutingRules.TrivialAck, result.Rule);
    }

    // ── Research questions and retired low signals ───────────────────────────

    [TestMethod]
    public void FirstTurn_ResearchQuestion_NotLowViaWhatSignals()
    {
        var result = _selector.Classify("what are the key features of the MCP version 2 spec", User());

        Assert.IsFalse(result.MatchedLowKeywords.Any(k => k.StartsWith("what", StringComparison.Ordinal)),
            $"No 'what …' low signal may match a research question (matched: [{string.Join(", ", result.MatchedLowKeywords)}]).");
        Assert.AreNotEqual(ModelTier.Low, result.Tier,
            "A research question about a named technical subject must not route Low.");
        Assert.AreEqual(TierRoutingRules.ResearchQuestionFloor, result.Rule);
    }

    [TestMethod]
    [DataRow("What is the capital of France?")]
    [DataRow("what's the weather?")]
    [DataRow("who was Abraham Lincoln?")]
    public void FirstTurn_Trivia_StaysLow(string prompt)
    {
        var result = _selector.Classify(prompt, User());

        Assert.AreEqual(ModelTier.Low, result.Tier, $"Trivia \"{prompt}\" is not floored.");
    }

    [TestMethod]
    [DataRow("what is")]
    [DataRow("what's")]
    [DataRow("tell me about")]
    [DataRow("look up")]
    [DataRow("show me")]
    [DataRow("i think")]
    [DataRow("i was thinking")]
    public void RetiredLowSignals_NotInCompiledDefaults(string phrase)
    {
        var result = _selector.Classify($"{phrase} something");

        CollectionAssert.DoesNotContain(result.MatchedLowKeywords.ToList(), phrase);
    }

    [TestMethod]
    public void RetiredLowSignals_FilteredFromLoadedConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "kts-retired-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // A dream (or a stale PVC file) lists the retired phrases — in mixed case and with a
            // typographic apostrophe — alongside a genuine addition.
            var config = """
            {
                "version": 1,
                "lowSignalKeywords": ["What Is", "what’s", "tell me about", "look up", "show me", "i think", "watching movie"]
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "tier-selector.json"), config);

            var options = Options.Create(new AgentProfileOptions { BasePath = tempDir });
            var selector = new KeywordTierSelector(options, NullLogger<KeywordTierSelector>.Instance);

            var retired = selector.Classify("tell me about what is in the new release, look up the notes and show me");
            Assert.AreEqual(0, retired.MatchedLowKeywords.Count,
                $"Retired phrases must not match even when the config lists them (matched: [{string.Join(", ", retired.MatchedLowKeywords)}]).");

            var addition = selector.Classify("I'm watching movie tonight");
            CollectionAssert.Contains(addition.MatchedLowKeywords.ToList(), "watching movie",
                "Non-retired dream additions still load.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("what is", true)]
    [DataRow("  WHAT'S ", true)]
    [DataRow("what’s", true)]
    [DataRow("what time", false)]
    [DataRow("thanks", false)]
    public void IsRetiredLowSignal_NormalisesCaseWhitespaceAndApostrophes(string keyword, bool expected)
    {
        Assert.AreEqual(expected, KeywordTierSelector.IsRetiredLowSignal(keyword));
    }

    // ── Rules reported for the base decision ─────────────────────────────────

    [TestMethod]
    public void NoContext_ReportsScoreBand()
    {
        var result = _selector.Classify("hello there");

        Assert.AreEqual(TierRoutingRules.ScoreBand, result.Rule);
        Assert.AreEqual(result.Tier, result.IntrinsicTier);
    }

    [TestMethod]
    public void FixedTierSelector_ReportsFixed()
    {
        var result = new FixedTierSelector(ModelTier.Low).Classify("do that", User(threadEstablished: true));

        Assert.AreEqual(ModelTier.Low, result.Tier, "A pinned tier ignores thread state.");
        Assert.AreEqual(TierRoutingRules.Fixed, result.Rule);
    }

    // ── Session replay (acceptance) ──────────────────────────────────────────

    [TestMethod]
    public void Replay_ProductionSession_DeckAndResearchTurnsRouteBalancedOrHigher()
    {
        // The user turns from the 2026-10-09 session, replayed with thread state as the handler
        // computes it: each exchange adds two conversation turns, and a thread is established
        // from ShortMessageHeuristics.ThreadEstablishedMinTurns prior turns. The first message
        // is a statement on a fresh session; every turn after it is research or deck work.
        string[] turns =
        [
            "you should now be running against the latest gpt models (terra and sol)",
            "what are the key features of the MCP version 2 spec",
            "why didn't you do a web search to find the details?",
            "ok, create a deck for me to review",
            "I see the outline - 30 slides is a lot - I typically estimate 5-6 minutes per slide",
            "figure out a way to update the doc",
            "so do it now",
            "do that",
        ];

        var recent = new List<ModelTier>();
        for (var i = 0; i < turns.Length; i++)
        {
            var priorTurns = i * 2;
            var context = User(
                threadEstablished: priorTurns >= ShortMessageHeuristics.ThreadEstablishedMinTurns,
                recentMaxTier: recent.Count > 0 ? recent.TakeLast(3).Max() : null);
            var result = _selector.Classify(turns[i], context);
            recent.Add(result.IntrinsicTier ?? result.Tier);

            if (i > 0)
                Assert.IsTrue(result.Tier >= ModelTier.Balanced,
                    $"Turn {i + 1} \"{turns[i]}\" routed {result.Tier} (rule={result.Rule}, score={result.ComplexityScore:F3}).");
        }
    }
}

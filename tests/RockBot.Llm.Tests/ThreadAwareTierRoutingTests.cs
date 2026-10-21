using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RockBot.Host;
using RockBot.Llm;

namespace RockBot.Llm.Tests;

/// <summary>
/// Thread-aware tier routing (#663, #729): inherited tiers that decay, casual threads that
/// stay Low, the active-subagent floor, the research-question floor, the
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

    // ── Thread that earned Balanced vs. casual thread (#729) ─────────────────

    [TestMethod]
    public void EstablishedThread_34CharFollowUp_WithRecentBalancedTurn_RoutesBalanced()
    {
        // Production reproducer: 34 chars, just over the old 30-char gate, so it went Low —
        // and that Low turn rewrote a file from a partial read. The thread had earned Balanced
        // (deck work), so the follow-up inherits it.
        const string prompt = "figure out a way to update the doc";
        Assert.AreEqual(34, prompt.Length, "Test premise: the production message length.");

        var result = _selector.Classify(prompt,
            User(threadEstablished: true, recentMaxTier: ModelTier.Balanced));

        Assert.AreEqual(ModelTier.Balanced, result.Tier);
        Assert.AreEqual(TierRoutingRules.InheritedTier, result.Rule);
        Assert.AreEqual(ModelTier.Low, result.IntrinsicTier,
            "Recorded intrinsically as Low, so the thread decays unless this turn does work.");
    }

    [TestMethod]
    [DataRow("ok, create a deck for me to review")]
    [DataRow("I see the outline - 30 slides is a lot - I typically estimate 5-6 minutes per slide")]
    [DataRow("so do it now")]
    [DataRow("do that")]
    [DataRow("why didn't you do a web search to find the details?")]
    [DataRow("yes")]
    [DataRow("go ahead")]
    public void EarnedThread_WorkInstructions_NeverRouteLow(string prompt)
    {
        var result = _selector.Classify(prompt,
            User(threadEstablished: true, recentMaxTier: ModelTier.Balanced));

        Assert.AreNotEqual(ModelTier.Low, result.Tier,
            $"\"{prompt}\" continues the thread's work and must not route Low (rule={result.Rule}).");
    }

    [TestMethod]
    [DataRow("I ordered the propane")]
    [DataRow("we're trying to schedule the ablation for January")]
    [DataRow("I'll find out soon")]
    [DataRow("any plans for the weekend?")]
    public void CasualThread_NonAckFollowUp_StaysLow(string prompt)
    {
        // Small talk past turn 3, on a thread whose recent turns all earned Low and did no work.
        // Until #729 the active-thread floor raised these to Balanced.
        foreach (var recent in new ModelTier?[] { null, ModelTier.Low })
        {
            var result = _selector.Classify(prompt, User(threadEstablished: true, recentMaxTier: recent));

            Assert.AreEqual(ModelTier.Low, result.Tier, $"\"{prompt}\" (recentMaxTier={recent}) rule={result.Rule}");
            Assert.AreEqual(TierRoutingRules.ScoreBand, result.Rule);
        }
    }

    [TestMethod]
    [DataRow("ok thanks")]
    [DataRow("thanks!")]
    [DataRow("got it")]
    [DataRow("ok")]
    [DataRow("👍")]
    [DataRow("Thanks so much, that's great")]
    [DataRow("good morning")]
    public void EarnedThread_TrivialAck_MayStayLow(string prompt)
    {
        var result = _selector.Classify(prompt,
            User(threadEstablished: true, recentMaxTier: ModelTier.Balanced));

        Assert.AreEqual(ModelTier.Low, result.Tier, $"\"{prompt}\" is a pure acknowledgement.");
        Assert.AreEqual(TierRoutingRules.TrivialAck, result.Rule,
            "The log must say the inherited tier was skipped because the message is a trivial ack.");
    }

    [TestMethod]
    public void ThreadEstablished_AloneNoLongerFloors()
    {
        var result = _selector.Classify("figure out a way to update the doc", User(threadEstablished: true));

        Assert.AreEqual(ModelTier.Low, result.Tier);
        Assert.AreNotEqual(TierRoutingRules.ActiveThreadFloor, result.Rule, "The active-thread floor was removed in #729.");
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
        Assert.AreEqual(ModelTier.Low, result.IntrinsicTier,
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
        Assert.AreEqual(ModelTier.Low, result.IntrinsicTier,
            "Thread-state floors are not part of the intrinsic tier, so they don't keep the thread sticky (#729).");
    }

    [TestMethod]
    public void ResearchQuestionFloor_CountsTowardIntrinsicTier()
    {
        var result = _selector.Classify("what are the key features of the MCP version 2 spec",
            User(threadEstablished: true));

        Assert.AreEqual(ModelTier.Balanced, result.IntrinsicTier,
            "Content floors describe the message itself, so they are what a thread inherits.");
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
        // History records each turn's intrinsic tier, raised to Balanced when the turn did work
        // on the user's behalf (web search, file writes) as the handler does since #729.
        (string Prompt, bool DidWork)[] turns =
        [
            ("you should now be running against the latest gpt models (terra and sol)", false),
            ("what are the key features of the MCP version 2 spec", true),
            ("why didn't you do a web search to find the details?", true),
            ("ok, create a deck for me to review", true),
            ("I see the outline - 30 slides is a lot - I typically estimate 5-6 minutes per slide", true),
            ("figure out a way to update the doc", true),
            ("so do it now", true),
            ("do that", true),
        ];

        var recent = new List<ModelTier>();
        for (var i = 0; i < turns.Length; i++)
        {
            var priorTurns = i * 2;
            var context = User(
                threadEstablished: priorTurns >= ShortMessageHeuristics.ThreadEstablishedMinTurns,
                recentMaxTier: recent.Count > 0 ? recent.TakeLast(SessionTierHistory.MaxTurnsPerSession).Max() : null);
            var result = _selector.Classify(turns[i].Prompt, context);
            var recorded = result.IntrinsicTier ?? result.Tier;
            if (turns[i].DidWork && recorded < ModelTier.Balanced)
                recorded = ModelTier.Balanced;
            recent.Add(recorded);

            if (i > 0)
                Assert.IsTrue(result.Tier >= ModelTier.Balanced,
                    $"Turn {i + 1} \"{turns[i].Prompt}\" routed {result.Tier} (rule={result.Rule}, score={result.ComplexityScore:F3}).");
        }
    }

    [TestMethod]
    public void Replay_CasualSession_StaysLowPastTurnThree()
    {
        // Small talk with no tool work: nothing earns Balanced, so nothing is inherited (#729).
        string[] turns =
        [
            "good morning",
            "I ordered the propane",
            "right now I'm in a-flutter, not a-fib - so the symptoms aren't too bad, mostly fatigue.",
            "we're trying to schedule the ablation for January",
            "I'll find out soon",
        ];

        var history = new SessionTierHistory();
        for (var i = 0; i < turns.Length; i++)
        {
            var result = _selector.Classify(turns[i], User(
                threadEstablished: i * 2 >= ShortMessageHeuristics.ThreadEstablishedMinTurns,
                recentMaxTier: history.GetRecentMax("s")));
            history.Record("s", $"t{i}", result.IntrinsicTier ?? result.Tier);

            Assert.AreEqual(ModelTier.Low, result.Tier,
                $"Turn {i + 1} \"{turns[i]}\" routed {result.Tier} (rule={result.Rule}, score={result.ComplexityScore:F3}).");
        }
    }

    [TestMethod]
    public void BalancedTurn_FollowUpsInherit_ThenDecayToLow()
    {
        var history = new SessionTierHistory();

        // A turn that earned Balanced on its own content.
        var research = _selector.Classify("what are the key features of the MCP version 2 spec", User());
        Assert.AreEqual(ModelTier.Balanced, research.IntrinsicTier);
        history.Record("s", "t0", research.IntrinsicTier!.Value);

        // Short non-ack follow-ups that do no work inherit Balanced for MaxTurnsPerSession turns...
        string[] followUps = ["interesting", "I'll find out soon", "my brother is visiting", "we'll grab dinner later"];
        for (var i = 0; i < followUps.Length; i++)
        {
            var result = _selector.Classify(followUps[i], User(threadEstablished: true, recentMaxTier: history.GetRecentMax("s")));
            history.Record("s", $"t{i + 1}", result.IntrinsicTier ?? result.Tier);

            var expected = i < SessionTierHistory.MaxTurnsPerSession ? ModelTier.Balanced : ModelTier.Low;
            Assert.AreEqual(expected, result.Tier,
                $"Follow-up {i + 1} \"{followUps[i]}\" (rule={result.Rule}).");
        }
    }
}

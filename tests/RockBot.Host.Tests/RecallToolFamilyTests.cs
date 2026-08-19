using System.Reflection;
using RockBot.Host;
using RockBot.Memory;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers <see cref="RecallTools"/> and the family discipline it exists to enforce: three
/// sibling search tools whose descriptions have to be tellable apart at a glance, and whose
/// empty results have to route a mis-aimed lookup at the right sibling instead of dead-ending.
/// </summary>
/// <remarks>
/// The three tools live in two assemblies, so nothing but a test like this notices when one
/// of them drifts out of the family — descriptions are string literals no compiler checks.
/// Descriptions are read by reflection rather than by constructing the tools, since the
/// attribute is the thing under test and construction would drag in three unrelated stores.
/// </remarks>
[TestClass]
public class RecallToolFamilyTests
{
    private static string DescriptionOf(Type type, string method) =>
        type.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!
            .Description;

    private static string DurableDescription =>
        DescriptionOf(typeof(MemoryTools), nameof(MemoryTools.SearchMemory));

    private static string WorkingDescription =>
        DescriptionOf(typeof(WorkingMemoryTools), nameof(WorkingMemoryTools.SearchWorkingMemory));

    private static string ConversationDescription =>
        DescriptionOf(typeof(ConversationRecallTools), nameof(ConversationRecallTools.SearchConversationHistory));

    // ── Scope headline ────────────────────────────────────────────────────

    [TestMethod]
    public void EachDescription_LeadsWithItsScopeHeadline()
    {
        StringAssert.StartsWith(DurableDescription, RecallTools.DurableHeadline);
        StringAssert.StartsWith(WorkingDescription, RecallTools.WorkingHeadline);
        StringAssert.StartsWith(ConversationDescription, RecallTools.ConversationHeadline);
    }

    [TestMethod]
    public void Headlines_AreDistinctFromEachOther()
    {
        var headlines = new[]
        {
            RecallTools.DurableHeadline,
            RecallTools.WorkingHeadline,
            RecallTools.ConversationHeadline
        };

        CollectionAssert.AllItemsAreUnique(headlines,
            "The headline is the only part of the description a model is guaranteed to read " +
            "when scanning three similar tools — two that match defeat the purpose.");
    }

    // ── Cross-references ──────────────────────────────────────────────────

    [TestMethod]
    public void EachDescription_NamesTheOtherTwoTools()
    {
        StringAssert.Contains(DurableDescription, RecallTools.WorkingMemory);
        StringAssert.Contains(DurableDescription, RecallTools.ConversationHistory);

        StringAssert.Contains(WorkingDescription, RecallTools.DurableMemory);
        StringAssert.Contains(WorkingDescription, RecallTools.ConversationHistory);

        StringAssert.Contains(ConversationDescription, RecallTools.DurableMemory);
        StringAssert.Contains(ConversationDescription, RecallTools.WorkingMemory);
    }

    [TestMethod]
    public void RegisteredToolName_MatchesTheSharedConstant()
    {
        // The directives, the docs, and both sibling descriptions all spell this name out;
        // the text-based tool-calling path resolves it by exact match.
        Assert.AreEqual(RecallTools.ConversationHistory, ConversationRecallTools.ToolName);
    }

    // ── LookElsewhere ─────────────────────────────────────────────────────

    [TestMethod]
    public void LookElsewhere_NamesTheOtherTwoToolsButNotTheCaller()
    {
        var hint = RecallTools.LookElsewhere(RecallTools.DurableMemory);

        StringAssert.Contains(hint, RecallTools.WorkingMemory);
        StringAssert.Contains(hint, RecallTools.ConversationHistory);
        Assert.IsFalse(hint.Contains($"use {RecallTools.DurableMemory}"),
            "Suggesting the tool that just came back empty is a loop, not a recovery.");
    }

    [TestMethod]
    public void LookElsewhere_WorksFromEveryMemberOfTheFamily()
    {
        foreach (var caller in new[]
                 {
                     RecallTools.DurableMemory,
                     RecallTools.WorkingMemory,
                     RecallTools.ConversationHistory
                 })
        {
            var hint = RecallTools.LookElsewhere(caller);

            Assert.IsFalse(hint.Contains($"use {caller}"), $"{caller} suggested itself");
            Assert.AreEqual(2, CountToolMentions(hint), $"{caller} should suggest exactly two siblings");
        }
    }

    [TestMethod]
    public void LookElsewhere_SaysAnEmptyResultIsNotProofOfNeverKnowing()
    {
        // The whole failure this family guards against is the agent reading silence as
        // "I was never told this" — the sentence carrying that has to survive re-wording.
        var hint = RecallTools.LookElsewhere(RecallTools.ConversationHistory);

        StringAssert.Contains(hint, "not evidence");
    }

    private static int CountToolMentions(string hint) =>
        new[] { RecallTools.DurableMemory, RecallTools.WorkingMemory, RecallTools.ConversationHistory }
            .Count(t => hint.Contains($"use {t}"));
}

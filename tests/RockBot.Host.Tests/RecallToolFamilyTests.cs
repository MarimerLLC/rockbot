using System.Reflection;
using RockBot.Host;
using RockBot.Memory;

namespace RockBot.Host.Tests;

/// <summary>
/// Covers <see cref="RecallTools"/> and the family discipline it exists to enforce: sibling
/// search tools whose descriptions have to be tellable apart at a glance, and whose empty
/// results have to route a mis-aimed lookup at the right sibling instead of dead-ending.
/// </summary>
/// <remarks>
/// The tools are registered from assemblies that do not reference each other, so nothing but a
/// test like this notices when one of them drifts out of the family — descriptions are string
/// literals no compiler checks. Descriptions are read by reflection rather than by constructing
/// the tools, since the attribute is the thing under test and construction would drag in
/// unrelated stores.
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

    /// <summary>
    /// Every member of the family. A third — <c>search_conversation_history</c>, tracked by
    /// #509 and blocked on #530 — appends here and the assertions below cover it unchanged.
    /// </summary>
    private static readonly string[] Family = [RecallTools.DurableMemory, RecallTools.WorkingMemory];

    // ── Scope headline ────────────────────────────────────────────────────

    [TestMethod]
    public void EachDescription_LeadsWithItsScopeHeadline()
    {
        StringAssert.StartsWith(DurableDescription, RecallTools.DurableHeadline);
        StringAssert.StartsWith(WorkingDescription, RecallTools.WorkingHeadline);
    }

    [TestMethod]
    public void Headlines_AreDistinctFromEachOther()
    {
        var headlines = new[]
        {
            RecallTools.DurableHeadline,
            RecallTools.WorkingHeadline
        };

        CollectionAssert.AllItemsAreUnique(headlines,
            "The headline is the only part of the description a model is guaranteed to read " +
            "when scanning similar tools — two that match defeat the purpose.");
    }

    // ── Cross-references ──────────────────────────────────────────────────

    [TestMethod]
    public void EachDescription_NamesItsSiblings()
    {
        StringAssert.Contains(DurableDescription, RecallTools.WorkingMemory);
        StringAssert.Contains(WorkingDescription, RecallTools.DurableMemory);
    }

    // ── LookElsewhere ─────────────────────────────────────────────────────

    [TestMethod]
    public void LookElsewhere_NamesTheSiblingButNotTheCaller()
    {
        var hint = RecallTools.LookElsewhere(RecallTools.DurableMemory);

        StringAssert.Contains(hint, RecallTools.WorkingMemory);
        Assert.IsFalse(hint.Contains($"use {RecallTools.DurableMemory}"),
            "Suggesting the tool that just came back empty is a loop, not a recovery.");
    }

    [TestMethod]
    public void LookElsewhere_WorksFromEveryMemberOfTheFamily()
    {
        foreach (var caller in Family)
        {
            var hint = RecallTools.LookElsewhere(caller);

            Assert.IsFalse(hint.Contains($"use {caller}"), $"{caller} suggested itself");
            Assert.AreEqual(Family.Length - 1, CountToolMentions(hint),
                $"{caller} should suggest every sibling and nothing else");
        }
    }

    [TestMethod]
    public void LookElsewhere_SaysAnEmptyResultIsNotProofOfNeverKnowing()
    {
        // The whole failure this family guards against is the agent reading silence as
        // "I was never told this" — the sentence carrying that has to survive re-wording.
        var hint = RecallTools.LookElsewhere(RecallTools.WorkingMemory);

        StringAssert.Contains(hint, "not evidence");
    }

    private static int CountToolMentions(string hint) =>
        Family.Count(t => hint.Contains($"use {t}"));
}

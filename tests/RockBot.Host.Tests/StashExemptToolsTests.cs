using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Guards the retrieve→re-stash loop described on <see cref="StashExemptTools"/>.
/// After issue #484 folded <c>list_working_memory</c> into <c>search_working_memory</c>,
/// the exemption has to cover the query-less listing path too — it arrives under the
/// search_working_memory name, so the entry that matters is that one.
/// </summary>
[TestClass]
public class StashExemptToolsTests
{
    [TestMethod]
    public void SearchWorkingMemory_IsExempt()
    {
        Assert.IsTrue(StashExemptTools.Contains("search_working_memory"),
            "Both the ranked-search and the folded-in listing path run under this name.");
    }

    [TestMethod]
    public void GetFromWorkingMemory_IsExempt() =>
        Assert.IsTrue(StashExemptTools.Contains("get_from_working_memory"));

    [TestMethod]
    public void MatchIsCaseInsensitive() =>
        Assert.IsTrue(StashExemptTools.Contains("SEARCH_WORKING_MEMORY"));

    [TestMethod]
    public void LegacyPascalCaseNames_RemainExempt()
    {
        // Issue #493 pinned these tools to snake_case. The pre-rename names stay in the set
        // on purpose: this is the guard against a non-terminating retrieve→re-stash loop, so
        // a stale name from an in-flight request during a rolling deploy must still match.
        Assert.IsTrue(StashExemptTools.Contains("GetFromWorkingMemory"));
        Assert.IsTrue(StashExemptTools.Contains("SearchWorkingMemory"));
    }

    [TestMethod]
    public void RemovedListTool_IsNoLongerListed() =>
        Assert.IsFalse(StashExemptTools.Contains("list_working_memory"),
            "The tool no longer exists; a stale entry would only mislead.");

    [TestMethod]
    public void UnrelatedTool_IsNotExempt() =>
        Assert.IsFalse(StashExemptTools.Contains("save_to_working_memory"));

    [TestMethod]
    public void NullOrEmpty_IsNotExempt()
    {
        Assert.IsFalse(StashExemptTools.Contains(null));
        Assert.IsFalse(StashExemptTools.Contains(string.Empty));
    }
}

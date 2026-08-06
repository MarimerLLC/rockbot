using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Guards the retrieve→re-stash loop described on <see cref="StashExemptTools"/>.
/// After issue #484 folded <c>list_working_memory</c> into <c>search_working_memory</c>,
/// the exemption has to cover the query-less listing path too — it arrives under the
/// SearchWorkingMemory name, so the entry that matters is that one.
/// </summary>
[TestClass]
public class StashExemptToolsTests
{
    [TestMethod]
    public void SearchWorkingMemory_IsExempt()
    {
        Assert.IsTrue(StashExemptTools.Contains("SearchWorkingMemory"),
            "Both the ranked-search and the folded-in listing path run under this name.");
    }

    [TestMethod]
    public void GetFromWorkingMemory_IsExempt() =>
        Assert.IsTrue(StashExemptTools.Contains("GetFromWorkingMemory"));

    [TestMethod]
    public void MatchIsCaseInsensitive() =>
        Assert.IsTrue(StashExemptTools.Contains("searchworkingmemory"));

    [TestMethod]
    public void RemovedListTool_IsNoLongerListed() =>
        Assert.IsFalse(StashExemptTools.Contains("ListWorkingMemory"),
            "The tool no longer exists; a stale entry would only mislead.");

    [TestMethod]
    public void UnrelatedTool_IsNotExempt() =>
        Assert.IsFalse(StashExemptTools.Contains("SaveToWorkingMemory"));

    [TestMethod]
    public void NullOrEmpty_IsNotExempt()
    {
        Assert.IsFalse(StashExemptTools.Contains(null));
        Assert.IsFalse(StashExemptTools.Contains(string.Empty));
    }
}

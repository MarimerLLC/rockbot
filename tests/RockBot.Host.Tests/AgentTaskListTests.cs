namespace RockBot.Host.Tests;

[TestClass]
public class AgentTaskListTests
{
    [TestMethod]
    public void IsEmpty_WhenNew_ReturnsTrue()
    {
        var list = new AgentTaskList();
        Assert.IsTrue(list.IsEmpty);
        Assert.IsFalse(list.HasUnfinishedItems);
        Assert.AreEqual(0, list.Snapshot().Count);
    }

    [TestMethod]
    public void CreateOrReplace_AssignsSequentialIdsStartingAtOne()
    {
        var list = new AgentTaskList();

        var created = list.CreateOrReplace(["read doc", "search emails", "draft response"]);

        Assert.AreEqual(3, created.Count);
        Assert.AreEqual(1, created[0].Id);
        Assert.AreEqual(2, created[1].Id);
        Assert.AreEqual(3, created[2].Id);
        Assert.AreEqual("read doc", created[0].Description);
        Assert.AreEqual(AgentTaskList.StatusPending, created[0].Status);
    }

    [TestMethod]
    public void CreateOrReplace_TrimsWhitespaceAndSkipsBlankItems()
    {
        var list = new AgentTaskList();

        var created = list.CreateOrReplace(["  step one  ", "", "   ", "step two"]);

        Assert.AreEqual(2, created.Count);
        Assert.AreEqual("step one", created[0].Description);
        Assert.AreEqual("step two", created[1].Description);
        Assert.AreEqual(1, created[0].Id);
        Assert.AreEqual(2, created[1].Id);
    }

    [TestMethod]
    public void CreateOrReplace_ResetsExistingState()
    {
        var list = new AgentTaskList();
        list.CreateOrReplace(["a", "b", "c"]);
        list.Update(1, AgentTaskList.StatusCompleted);

        var created = list.CreateOrReplace(["x", "y"]);

        Assert.AreEqual(2, created.Count);
        Assert.AreEqual(1, created[0].Id);
        Assert.AreEqual("x", created[0].Description);
        Assert.AreEqual(AgentTaskList.StatusPending, created[0].Status,
            "Replacing the list should reset all statuses to pending");
    }

    [TestMethod]
    public void CreateOrReplace_WithEmptyEnumerable_ClearsList()
    {
        var list = new AgentTaskList();
        list.CreateOrReplace(["a", "b"]);

        var created = list.CreateOrReplace(Array.Empty<string>());

        Assert.AreEqual(0, created.Count);
        Assert.IsTrue(list.IsEmpty);
    }

    [TestMethod]
    public void Update_KnownId_UpdatesStatusAndReturnsItem()
    {
        var list = new AgentTaskList();
        list.CreateOrReplace(["a", "b"]);

        var updated = list.Update(2, AgentTaskList.StatusInProgress);

        Assert.IsNotNull(updated);
        Assert.AreEqual(2, updated.Id);
        Assert.AreEqual(AgentTaskList.StatusInProgress, updated.Status);
        Assert.AreEqual("b", updated.Description);

        // Underlying snapshot reflects the change.
        var snapshot = list.Snapshot();
        Assert.AreEqual(AgentTaskList.StatusPending, snapshot[0].Status);
        Assert.AreEqual(AgentTaskList.StatusInProgress, snapshot[1].Status);
    }

    [TestMethod]
    public void Update_UnknownId_ReturnsNull()
    {
        var list = new AgentTaskList();
        list.CreateOrReplace(["a"]);

        var updated = list.Update(99, AgentTaskList.StatusCompleted);

        Assert.IsNull(updated);
    }

    [TestMethod]
    public void Update_InvalidStatus_Throws()
    {
        var list = new AgentTaskList();
        list.CreateOrReplace(["a"]);

        Assert.ThrowsExactly<ArgumentException>(() => list.Update(1, "blocked"));
    }

    [TestMethod]
    public void Update_StatusCaseInsensitive_NormalisedToLowercase()
    {
        var list = new AgentTaskList();
        list.CreateOrReplace(["a"]);

        var updated = list.Update(1, "IN_PROGRESS");

        Assert.IsNotNull(updated);
        Assert.AreEqual(AgentTaskList.StatusInProgress, updated.Status);
    }

    [TestMethod]
    public void HasUnfinishedItems_TrueWhilePendingOrInProgress()
    {
        var list = new AgentTaskList();
        list.CreateOrReplace(["a", "b"]);

        Assert.IsTrue(list.HasUnfinishedItems, "All pending should count as unfinished");

        list.Update(1, AgentTaskList.StatusCompleted);
        Assert.IsTrue(list.HasUnfinishedItems, "One completed, one pending → unfinished");

        list.Update(2, AgentTaskList.StatusInProgress);
        Assert.IsTrue(list.HasUnfinishedItems, "in_progress counts as unfinished");

        list.Update(2, AgentTaskList.StatusCompleted);
        Assert.IsFalse(list.HasUnfinishedItems, "All completed → finished");
    }

    [TestMethod]
    public void Snapshot_IsIndependentCopy()
    {
        var list = new AgentTaskList();
        list.CreateOrReplace(["a"]);

        var snapshot = list.Snapshot();
        list.Update(1, AgentTaskList.StatusCompleted);

        Assert.AreEqual(AgentTaskList.StatusPending, snapshot[0].Status,
            "Snapshot should not reflect changes made after it was taken");
    }
}

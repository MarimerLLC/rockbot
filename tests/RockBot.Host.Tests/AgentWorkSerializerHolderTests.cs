namespace RockBot.Host.Tests;

/// <summary>
/// The serializer names what holds the slot so a refused scheduled acquisition can say why.
/// A 10:00 briefing that lost the slot to another 10:00 scheduled task was logged as
/// "preempted by active user session" when no user was online.
/// </summary>
[TestClass]
public class AgentWorkSerializerHolderTests
{
    [TestMethod]
    public void CurrentHolder_IsNull_WhenSlotIsFree()
    {
        using var serializer = new AgentWorkSerializer();

        Assert.IsNull(serializer.CurrentHolder);
    }

    [TestMethod]
    public async Task CurrentHolder_NamesTheScheduledTask_ThatHoldsTheSlot()
    {
        using var serializer = new AgentWorkSerializer();
        IAgentWorkSerializer work = serializer;

        var held = await work.TryAcquireForScheduledAsync("scheduled task 'cost-guard'", CancellationToken.None);
        Assert.IsNotNull(held);

        var refused = await work.TryAcquireForScheduledAsync("scheduled task 'brief'", CancellationToken.None);
        Assert.IsNull(refused);
        Assert.AreEqual("scheduled task 'cost-guard'", work.CurrentHolder);

        await held.DisposeAsync();
        Assert.IsNull(work.CurrentHolder);
    }

    [TestMethod]
    public async Task CurrentHolder_NamesTheUserLoop_ThatHoldsTheSlot()
    {
        using var serializer = new AgentWorkSerializer();
        IAgentWorkSerializer work = serializer;

        var held = await work.AcquireForUserAsync("user session 'blazor-session'", CancellationToken.None);
        Assert.AreEqual("user session 'blazor-session'", work.CurrentHolder);

        await held.DisposeAsync();
        Assert.IsNull(work.CurrentHolder);
    }

    [TestMethod]
    public async Task CurrentHolder_HasAGenericLabel_ForUnnamedAcquisitions()
    {
        using var serializer = new AgentWorkSerializer();

        var held = await serializer.TryAcquireForScheduledAsync(CancellationToken.None);
        Assert.IsNotNull(held);
        Assert.AreEqual("background work", serializer.CurrentHolder);
        await held.DisposeAsync();

        var user = await serializer.AcquireForUserAsync(CancellationToken.None);
        Assert.AreEqual("a user session", serializer.CurrentHolder);
        await user.DisposeAsync();
    }
}

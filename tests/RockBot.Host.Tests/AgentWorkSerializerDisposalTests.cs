namespace RockBot.Host.Tests;

/// <summary>
/// The scheduled-acquisition path runs from timer callbacks that can outlive host
/// shutdown, so it must report "no slot" on a disposed serializer rather than throwing
/// <see cref="ObjectDisposedException"/> out of an unobserved task (issue #494).
/// </summary>
[TestClass]
public class AgentWorkSerializerDisposalTests
{
    [TestMethod]
    public async Task TryAcquireForScheduled_ReturnsSlot_BeforeDisposal()
    {
        using var serializer = new AgentWorkSerializer();

        var slot = await serializer.TryAcquireForScheduledAsync(CancellationToken.None);

        Assert.IsNotNull(slot);
        await slot.DisposeAsync();
    }

    [TestMethod]
    public async Task TryAcquireForScheduled_AfterDispose_ReturnsNull()
    {
        var serializer = new AgentWorkSerializer();
        serializer.Dispose();

        var slot = await serializer.TryAcquireForScheduledAsync(CancellationToken.None);

        Assert.IsNull(slot);
    }

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        var serializer = new AgentWorkSerializer();

        serializer.Dispose();
        serializer.Dispose();
    }

    [TestMethod]
    public async Task TryAcquireForScheduled_ReturnsNull_WhileSlotIsHeld()
    {
        using var serializer = new AgentWorkSerializer();

        var held = await serializer.TryAcquireForScheduledAsync(CancellationToken.None);
        Assert.IsNotNull(held);

        // Moving the non-blocking wait inside the preemption lock must not change this:
        // a second scheduled acquisition still sees the slot as taken.
        var second = await serializer.TryAcquireForScheduledAsync(CancellationToken.None);
        Assert.IsNull(second);

        await held.DisposeAsync();

        var third = await serializer.TryAcquireForScheduledAsync(CancellationToken.None);
        Assert.IsNotNull(third);
        await third.DisposeAsync();
    }

    [TestMethod]
    public async Task ScheduledSlot_Dispose_AfterSerializerDisposed_DoesNotThrow()
    {
        var serializer = new AgentWorkSerializer();

        // A cycle that outran the host's shutdown timeout still holds its slot when
        // the container disposes the serializer. Handing the slot back must be a no-op,
        // not an ObjectDisposedException out of the cycle's finally block.
        var slot = await serializer.TryAcquireForScheduledAsync(CancellationToken.None);
        Assert.IsNotNull(slot);

        serializer.Dispose();

        await slot.DisposeAsync();
    }

    [TestMethod]
    public async Task UserSlot_Dispose_AfterSerializerDisposed_DoesNotThrow()
    {
        var serializer = new AgentWorkSerializer();

        var user = await serializer.AcquireForUserAsync(CancellationToken.None);

        serializer.Dispose();

        await user.DisposeAsync();
    }

    [TestMethod]
    public async Task TryAcquireForScheduled_ReturnsNull_WhileUserHoldsSlot()
    {
        using var serializer = new AgentWorkSerializer();

        var user = await serializer.AcquireForUserAsync(CancellationToken.None);

        var slot = await serializer.TryAcquireForScheduledAsync(CancellationToken.None);
        Assert.IsNull(slot);

        await user.DisposeAsync();
    }
}

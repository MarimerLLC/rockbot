namespace RockBot.A2A.Tests;

/// <summary>
/// The discovery queue is durable and per identity, so it must carry its own retention
/// or a stopped agent's queue grows without bound (issue #650).
/// </summary>
[TestClass]
public class AgentDiscoveryServiceTests
{
    [TestMethod]
    public void DiscoverySubscription_ExpiresStaleAnnouncementsAndIdleQueues()
    {
        var options = AgentDiscoveryService.DiscoverySubscriptionOptions;

        Assert.AreEqual(TimeSpan.FromMinutes(10), options.MessageTtl);
        Assert.AreEqual(TimeSpan.FromHours(24), options.IdleExpiry);
        Assert.IsFalse(options.DeadLetter, "Expired announcements would otherwise fill the DLQ");
        Assert.IsFalse(options.Ephemeral,
            "Deleting the shared queue on dispose would break the replacement pod in a rolling restart");
    }
}

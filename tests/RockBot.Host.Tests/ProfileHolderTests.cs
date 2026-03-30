namespace RockBot.Host.Tests;

[TestClass]
public class ProfileHolderTests
{
    [TestMethod]
    public void Profile_ThrowsBeforeFirstUpdate()
    {
        var holder = new ProfileHolder();
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = holder.Profile);
    }

    [TestMethod]
    public void Update_SetsProfileAndIncrementsVersion()
    {
        var holder = new ProfileHolder();
        Assert.AreEqual(0, holder.Version);

        var profile1 = MakeProfile("soul-1");
        holder.Update(profile1);

        Assert.AreEqual(1, holder.Version);
        Assert.AreSame(profile1, holder.Profile);

        var profile2 = MakeProfile("soul-2");
        holder.Update(profile2);

        Assert.AreEqual(2, holder.Version);
        Assert.AreSame(profile2, holder.Profile);
    }

    [TestMethod]
    public void Update_ThrowsOnNull()
    {
        var holder = new ProfileHolder();
        Assert.ThrowsExactly<ArgumentNullException>(() => holder.Update(null!));
    }

    private static AgentProfile MakeProfile(string soulContent) =>
        new(
            new AgentProfileDocument("soul", null, [], soulContent),
            new AgentProfileDocument("directives", null, [], "directives"));
}

namespace RockBot.Host.Tests;

[TestClass]
public class AgentNameHolderTests
{
    [TestMethod]
    public void DisplayName_DefaultsToNull()
    {
        var holder = new AgentNameHolder();
        Assert.IsNull(holder.DisplayName);
    }

    [TestMethod]
    public void Update_SetsDisplayNameAndIncrementsVersion()
    {
        var holder = new AgentNameHolder();
        Assert.AreEqual(0, holder.Version);

        holder.Update("MyBot");

        Assert.AreEqual(1, holder.Version);
        Assert.AreEqual("MyBot", holder.DisplayName);
    }

    [TestMethod]
    public void Update_TrimsWhitespace()
    {
        var holder = new AgentNameHolder();
        holder.Update("  SpacedBot  ");

        Assert.AreEqual("SpacedBot", holder.DisplayName);
    }

    [TestMethod]
    public void Update_NullClearsDisplayName()
    {
        var holder = new AgentNameHolder();
        holder.Update("MyBot");
        Assert.AreEqual("MyBot", holder.DisplayName);

        holder.Update(null);

        Assert.IsNull(holder.DisplayName);
        Assert.AreEqual(2, holder.Version);
    }

    [TestMethod]
    public void Update_EmptyStringClearsDisplayName()
    {
        var holder = new AgentNameHolder();
        holder.Update("MyBot");

        holder.Update("");

        Assert.IsNull(holder.DisplayName);
    }

    [TestMethod]
    public void Update_WhitespaceOnlyClearsDisplayName()
    {
        var holder = new AgentNameHolder();
        holder.Update("MyBot");

        holder.Update("   ");

        Assert.IsNull(holder.DisplayName);
    }

    [TestMethod]
    public void VersionIncrements_OnEachUpdate()
    {
        var holder = new AgentNameHolder();
        Assert.AreEqual(0, holder.Version);

        holder.Update("A");
        Assert.AreEqual(1, holder.Version);

        holder.Update("B");
        Assert.AreEqual(2, holder.Version);

        holder.Update(null);
        Assert.AreEqual(3, holder.Version);
    }
}

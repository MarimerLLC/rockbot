namespace RockBot.Host.Tests;

[TestClass]
public class FailureClusterTests
{
    [TestMethod]
    public void ClusterKey_LowercasesServerAndTool()
    {
        var key = new ClusterKey("Calendar-MCP", "Get_Calendar_Events", "timeZone");

        Assert.AreEqual("calendar-mcp", key.Server);
        Assert.AreEqual("get_calendar_events", key.Tool);
        Assert.AreEqual("timeZone", key.ErrorClass);
    }

    [TestMethod]
    public void ClusterKey_RejectsBlankComponents()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ClusterKey("", "tool", "class"));
        Assert.ThrowsExactly<ArgumentException>(() => new ClusterKey("server", " ", "class"));
        Assert.ThrowsExactly<ArgumentException>(() => new ClusterKey("server", "tool", ""));
    }

    [TestMethod]
    public void ClusterKey_EqualsByCanonicalisedComponents()
    {
        var a = new ClusterKey("Calendar-MCP", "Tool", "class");
        var b = new ClusterKey("calendar-mcp", "tool", "class");

        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }
}

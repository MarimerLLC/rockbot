using RockBot.Messaging;

namespace RockBot.Tools.Tests;

[TestClass]
public class WellKnownHeadersTests
{
    [TestMethod]
    public void ContentTrust_HasExpectedValue()
    {
        Assert.AreEqual("rb-content-trust", WellKnownHeaders.ContentTrust);
    }

    [TestMethod]
    public void ToolProvider_HasExpectedValue()
    {
        Assert.AreEqual("rb-tool-provider", WellKnownHeaders.ToolProvider);
    }

    [TestMethod]
    public void TimeoutMs_HasExpectedValue()
    {
        Assert.AreEqual("rb-timeout-ms", WellKnownHeaders.TimeoutMs);
    }

    [TestMethod]
    public void ContentTrustValues_System()
    {
        Assert.AreEqual("system", WellKnownHeaders.ContentTrustValues.System);
    }

    [TestMethod]
    public void ContentTrustValues_UserInput()
    {
        Assert.AreEqual("user-input", WellKnownHeaders.ContentTrustValues.UserInput);
    }

    [TestMethod]
    public void ContentTrustValues_ToolRequest()
    {
        Assert.AreEqual("tool-request", WellKnownHeaders.ContentTrustValues.ToolRequest);
    }

    [TestMethod]
    public void ContentTrustValues_ToolOutput()
    {
        Assert.AreEqual("tool-output", WellKnownHeaders.ContentTrustValues.ToolOutput);
    }

    [TestMethod]
    public void ContentTrustValues_AgentMessage()
    {
        Assert.AreEqual("agent-message", WellKnownHeaders.ContentTrustValues.AgentMessage);
    }
}

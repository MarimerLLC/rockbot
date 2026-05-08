using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Tests.Recovery;

[TestClass]
public class StageBLlmFillerTests
{
    [TestMethod]
    public void StripCodeFence_PassesThroughBareJson()
    {
        Assert.AreEqual("\"hello\"", StageBLlmFiller.StripCodeFence("\"hello\""));
    }

    [TestMethod]
    public void StripCodeFence_StripsTripleBacktickJson()
    {
        var input = "```json\n\"hello\"\n```";
        Assert.AreEqual("\"hello\"", StageBLlmFiller.StripCodeFence(input));
    }

    [TestMethod]
    public void StripCodeFence_StripsBareTripleBacktick()
    {
        var input = "```\n42\n```";
        Assert.AreEqual("42", StageBLlmFiller.StripCodeFence(input));
    }
}

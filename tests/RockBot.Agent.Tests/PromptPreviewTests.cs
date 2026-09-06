using RockBot.Agent;

namespace RockBot.Agent.Tests;

/// <summary>
/// Covers the routing-log prompt preview truncation (issue #556).
/// </summary>
[TestClass]
public class PromptPreviewTests
{
    [TestMethod]
    public void ToPromptPreview_ShorterThanLimit_ReturnsUnchanged()
    {
        const string prompt = "Hopefully we can go this coming winter. My health seems better now";
        Assert.AreEqual(prompt, UserMessageHandler.ToPromptPreview(prompt));
    }

    [TestMethod]
    public void ToPromptPreview_ExactlyAtLimit_ReturnsUnchanged()
    {
        var prompt = new string('a', 150);
        Assert.AreEqual(prompt, UserMessageHandler.ToPromptPreview(prompt));
    }

    [TestMethod]
    public void ToPromptPreview_OneOverLimit_TruncatesTo150()
    {
        var prompt = new string('a', 151);
        Assert.AreEqual(150, UserMessageHandler.ToPromptPreview(prompt).Length);
    }

    [TestMethod]
    public void ToPromptPreview_Empty_ReturnsEmpty()
    {
        Assert.AreEqual("", UserMessageHandler.ToPromptPreview(""));
    }
}

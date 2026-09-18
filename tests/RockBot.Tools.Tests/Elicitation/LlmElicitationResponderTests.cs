using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Tools.Tests.Elicitation;

[TestClass]
public class LlmElicitationResponderTests
{
    [TestMethod]
    public void Parse_ReadsAnAcceptedAnswer()
    {
        var answer = LlmElicitationResponder.Parse("""{"action":"accept","content":{"mailbox":"work","limit":10}}""");

        Assert.IsTrue(answer.Accepted);
        Assert.AreEqual("work", answer.Content!["mailbox"].GetString());
        Assert.AreEqual(10, answer.Content["limit"].GetInt32());
    }

    [TestMethod]
    public void Parse_ReadsAnAnswerWrappedInACodeFence()
    {
        var answer = LlmElicitationResponder.Parse(
            "Sure, here you go:\n```json\n{\"action\":\"accept\",\"content\":{\"mailbox\":\"work\"}}\n```");

        Assert.IsTrue(answer.Accepted);
        Assert.AreEqual("work", answer.Content!["mailbox"].GetString());
    }

    [TestMethod]
    public void Parse_KeepsTheDeclineReason()
    {
        var answer = LlmElicitationResponder.Parse("""{"action":"decline","reason":"the mailbox was never named"}""");

        Assert.IsFalse(answer.Accepted);
        Assert.AreEqual("the mailbox was never named", answer.Reason);
    }

    [TestMethod]
    public void Parse_DeclinesAnAcceptWithNoContent()
    {
        Assert.IsFalse(LlmElicitationResponder.Parse("""{"action":"accept"}""").Accepted);
        Assert.IsFalse(LlmElicitationResponder.Parse("""{"action":"accept","content":{}}""").Accepted);
    }

    [TestMethod]
    public void Parse_DeclinesWhenTheReplyIsNotJson()
    {
        var answer = LlmElicitationResponder.Parse("I'm not sure what it wants.");

        Assert.IsFalse(answer.Accepted);
        Assert.IsNotNull(answer.Reason);
    }

    [TestMethod]
    public void Parse_DeclinesWhenTheJsonIsMalformed()
        => Assert.IsFalse(LlmElicitationResponder.Parse("""{"action":"accept","content":{""").Accepted);

    [TestMethod]
    public void ExtractJsonObject_IgnoresBracesInsideStrings()
    {
        var json = LlmElicitationResponder.ExtractJsonObject("""{"action":"decline","reason":"it wanted {stuff}"}""");

        Assert.IsNotNull(json);
        StringAssert.EndsWith(json, "}");
        Assert.IsTrue(json.Contains("{stuff}", StringComparison.Ordinal));
    }
}

namespace RockBot.Host.Tests;

[TestClass]
public class ContextOverflowParserTests
{
    [TestMethod]
    public void OpenAiMessage_IsParsed()
    {
        var ok = ContextOverflowParser.TryParse(
            "This model's maximum context length is 128000 tokens. However, your messages resulted in 130500 tokens.",
            out var max, out var used);

        Assert.IsTrue(ok);
        Assert.AreEqual(128000, max);
        Assert.AreEqual(130500, used);
    }

    [TestMethod]
    public void AnthropicMessage_IsParsed()
    {
        // As surfaced by the Anthropic provider: status line, then the raw error body.
        var ok = ContextOverflowParser.TryParse(
            "Status Code: BadRequest\n{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\"," +
            "\"message\":\"prompt is too long: 208310 tokens > 200000 maximum\"}}",
            out var max, out var used);

        Assert.IsTrue(ok);
        Assert.AreEqual(200000, max);
        Assert.AreEqual(208310, used);
    }

    [TestMethod]
    public void UnrelatedMessage_IsNotParsed()
    {
        Assert.IsFalse(ContextOverflowParser.TryParse(
            "Your credit balance is too low to access the Anthropic API.", out _, out _));
    }
}

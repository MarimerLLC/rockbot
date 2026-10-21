namespace RockBot.Host.Tests;

[TestClass]
public sealed class LlmClientServedModelTests
{
    [TestMethod]
    [DataRow("claude-sonnet-5-5", null, "claude-sonnet-5-5")]
    [DataRow("claude-sonnet-5-5", "", "claude-sonnet-5-5")]
    [DataRow("claude-sonnet-5-5", "claude-sonnet-5-5", "claude-sonnet-5-5")]
    [DataRow("gpt-5.6-terra", "gpt-5.6-terra-2026-07-09", "gpt-5.6-terra")]
    [DataRow("GPT-5.6-Terra", "gpt-5.6-terra-2026-07-09", "GPT-5.6-Terra")]
    [DataRow("claude-sonnet-5-5", "gpt-5.6-terra-2026-07-09", "gpt-5.6-terra-2026-07-09")]
    public void ServedModelId_KeepsConfiguredId_UnlessAnotherModelAnswered(
        string configured, string? reported, string expected)
    {
        Assert.AreEqual(expected, LlmClient.ServedModelId(configured, reported));
    }
}

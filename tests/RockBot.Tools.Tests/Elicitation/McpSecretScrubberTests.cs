using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Tools.Tests.Elicitation;

[TestClass]
public class McpSecretScrubberTests
{
    [TestMethod]
    [DataRow("my key is sk-proj-AbCdEf0123456789XyZ ok", "sk-proj-AbCdEf0123456789XyZ", DisplayName = "OpenAI-style key")]
    [DataRow("token ghp_abcdefghijklmnopqrstuvwxyz0123 here", "ghp_abcdefghijklmnopqrstuvwxyz0123", DisplayName = "GitHub token")]
    [DataRow("aws AKIAABCDEFGHIJKLMNOP key", "AKIAABCDEFGHIJKLMNOP", DisplayName = "AWS access key")]
    [DataRow("Authorization: Bearer abc.def-ghi_jkl123456", "abc.def-ghi_jkl123456", DisplayName = "bearer token")]
    [DataRow("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpM", "eyJhbGciOiJIUzI1NiJ9", DisplayName = "JWT")]
    [DataRow("password: hunter2", "hunter2", DisplayName = "password assignment")]
    [DataRow("api_key=abc123def", "abc123def", DisplayName = "api_key assignment")]
    [DataRow("my pin is 4821", "4821", DisplayName = "pin")]
    [DataRow("card 4111 1111 1111 1111 exp", "4111 1111 1111 1111", DisplayName = "card number")]
    [DataRow("ssn 123-45-6789", "123-45-6789", DisplayName = "SSN")]
    [DataRow("opaque Zx9Qm2Lp7Rt4Vw8Ny3Kb6Hd1Fg5Js0Ae2", "Zx9Qm2Lp7Rt4Vw8Ny3Kb6Hd1Fg5Js0Ae2", DisplayName = "long mixed token")]
    public void Scrub_RedactsSecretShapedText(string input, string secret)
    {
        var scrubbed = McpSecretScrubber.Scrub(input);

        Assert.IsFalse(scrubbed.Contains(secret, StringComparison.Ordinal), scrubbed);
        StringAssert.Contains(scrubbed, McpSecretScrubber.Redacted);
    }

    [TestMethod]
    public void Scrub_RedactsAPrivateKeyBlock()
    {
        var scrubbed = McpSecretScrubber.Scrub(
            "here:\n-----BEGIN RSA PRIVATE KEY-----\nMIIEow\nIBAAK\n-----END RSA PRIVATE KEY-----\nthanks");

        Assert.IsFalse(scrubbed.Contains("MIIEow", StringComparison.Ordinal));
        StringAssert.Contains(scrubbed, "thanks");
    }

    [TestMethod]
    [DataRow("I want to research Mercury, the planet, for a telescope night in March 2027.")]
    [DataRow("Compare the 3 options: rent, buy, or wait 12 months.")]
    [DataRow("The meeting is at 10:30 on 2026-09-25.")]
    public void Scrub_LeavesOrdinaryTextAlone(string input)
        => Assert.AreEqual(input, McpSecretScrubber.Scrub(input));

    [TestMethod]
    public void Scrub_HandlesNullAndEmpty()
    {
        Assert.AreEqual(string.Empty, McpSecretScrubber.Scrub(null));
        Assert.AreEqual(string.Empty, McpSecretScrubber.Scrub(string.Empty));
    }
}

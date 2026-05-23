using RockBot.Tools.Mcp.Auth;

namespace RockBot.Tools.Tests.Auth;

[TestClass]
public class WwwAuthenticateChallengeTests
{
    [TestMethod]
    public void TryParse_McpResourceMetadataChallenge_ExtractsUrl()
    {
        var header = "Bearer resource_metadata=\"https://example.com/.well-known/oauth-protected-resource\"";

        Assert.IsTrue(WwwAuthenticateChallenge.TryParse(header, out var challenge));
        Assert.AreEqual("Bearer", challenge.Scheme);
        Assert.AreEqual("https://example.com/.well-known/oauth-protected-resource", challenge.ResourceMetadata);
        Assert.AreEqual(header, challenge.RawValue);
    }

    [TestMethod]
    public void TryParse_Rfc6750ErrorChallenge_ExtractsErrorDetails()
    {
        var header = "Bearer realm=\"example\", error=\"invalid_token\", error_description=\"The access token expired\"";

        Assert.IsTrue(WwwAuthenticateChallenge.TryParse(header, out var challenge));
        Assert.AreEqual("Bearer", challenge.Scheme);
        Assert.AreEqual("example", challenge.Realm);
        Assert.AreEqual("invalid_token", challenge.Error);
        Assert.AreEqual("The access token expired", challenge.ErrorDescription);
    }

    [TestMethod]
    public void TryParse_CombinedMcpAndRfc6750_ExtractsBoth()
    {
        var header = "Bearer resource_metadata=\"https://srv/.well-known/oauth-protected-resource\", " +
                     "error=\"invalid_token\", error_description=\"Token revoked\", " +
                     "scope=\"WorkIQ.Mail.Read WorkIQ.Calendar.Read\"";

        Assert.IsTrue(WwwAuthenticateChallenge.TryParse(header, out var challenge));
        Assert.AreEqual("https://srv/.well-known/oauth-protected-resource", challenge.ResourceMetadata);
        Assert.AreEqual("invalid_token", challenge.Error);
        Assert.AreEqual("Token revoked", challenge.ErrorDescription);
        Assert.AreEqual("WorkIQ.Mail.Read WorkIQ.Calendar.Read", challenge.Scope);
    }

    [TestMethod]
    public void TryParse_SchemeOnly_ParsesWithNoParameters()
    {
        Assert.IsTrue(WwwAuthenticateChallenge.TryParse("Bearer", out var challenge));
        Assert.AreEqual("Bearer", challenge.Scheme);
        Assert.IsNull(challenge.ResourceMetadata);
        Assert.IsNull(challenge.Error);
    }

    [TestMethod]
    public void TryParse_UnquotedValues_ParsesCorrectly()
    {
        // RFC 7235 permits token values without quotes for simple identifiers
        var header = "Bearer realm=example, error=invalid_token";

        Assert.IsTrue(WwwAuthenticateChallenge.TryParse(header, out var challenge));
        Assert.AreEqual("example", challenge.Realm);
        Assert.AreEqual("invalid_token", challenge.Error);
    }

    [TestMethod]
    public void TryParse_EscapedQuoteInValue_IsUnescaped()
    {
        var header = "Bearer error_description=\"Token \\\"expired\\\" yesterday\"";

        Assert.IsTrue(WwwAuthenticateChallenge.TryParse(header, out var challenge));
        Assert.AreEqual("Token \"expired\" yesterday", challenge.ErrorDescription);
    }

    [TestMethod]
    public void TryParse_NullOrWhitespace_ReturnsFalse()
    {
        Assert.IsFalse(WwwAuthenticateChallenge.TryParse(null, out _));
        Assert.IsFalse(WwwAuthenticateChallenge.TryParse("", out _));
        Assert.IsFalse(WwwAuthenticateChallenge.TryParse("   ", out _));
    }

    [TestMethod]
    public void TryParse_GarbageWithoutKeyValuePairs_ReturnsSchemeOnly()
    {
        // We accept the scheme even when parameters are missing or malformed,
        // because servers in the wild send all kinds of nonsense and crashing
        // the bridge over a malformed header would be worse than logging it.
        Assert.IsTrue(WwwAuthenticateChallenge.TryParse("Bearer not-a-valid-param-section", out var challenge));
        Assert.AreEqual("Bearer", challenge.Scheme);
        Assert.IsNull(challenge.ResourceMetadata);
    }
}

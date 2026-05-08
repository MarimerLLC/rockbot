using RockBot.Memory;

namespace RockBot.Host.Tests;

[TestClass]
public class ObservationLanguageDetectorTests
{
    [TestMethod]
    [DataRow("the calendar wrapper is blocked", true)]
    [DataRow("we cannot reach the bridge", true)]
    [DataRow("Cannot pass arguments", true)]
    [DataRow("This looks like a wrapper limitation", true)]
    [DataRow("get_calendar_events is not supported by this account", true)]
    [DataRow("the tool does not expose the timeZone field", true)]
    [DataRow("CANNOT pass arguments", true)] // case-insensitive
    [DataRow("Wrapper Limitation in the bridge", true)]
    [DataRow("does NOT expose the field", true)]
    public void LooksLikeCapabilityClaim_PositiveMatches(string content, bool expected)
    {
        Assert.AreEqual(expected, ObservationLanguageDetector.LooksLikeCapabilityClaim(content));
    }

    [TestMethod]
    [DataRow("user prefers concise answers")]
    [DataRow("project deadline is May 30")]
    [DataRow("standup at 10am")]
    [DataRow("uncannily fast response")] // contains "cann" but not the word "cannot"
    [DataRow("the road is unblocked now")] // "unblocked" contains "blocked" — false positive risk
    public void LooksLikeCapabilityClaim_NegativeMatches(string content)
    {
        Assert.IsFalse(ObservationLanguageDetector.LooksLikeCapabilityClaim(content),
            $"Content '{content}' should not match — bare nouns/verbs without claim semantics.");
    }

    [TestMethod]
    public void LooksLikeCapabilityClaim_NullEmptyOrWhitespace_ReturnsFalse()
    {
        Assert.IsFalse(ObservationLanguageDetector.LooksLikeCapabilityClaim(null));
        Assert.IsFalse(ObservationLanguageDetector.LooksLikeCapabilityClaim(""));
        Assert.IsFalse(ObservationLanguageDetector.LooksLikeCapabilityClaim("   \t\n"));
    }

    [TestMethod]
    public void LooksLikeCapabilityClaim_KeywordSurroundedByPunctuation_StillMatches()
    {
        Assert.IsTrue(ObservationLanguageDetector.LooksLikeCapabilityClaim("(blocked!)"));
        Assert.IsTrue(ObservationLanguageDetector.LooksLikeCapabilityClaim("status: cannot."));
    }
}

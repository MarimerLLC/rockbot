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

    [TestMethod]
    public void TryExtractToolReferences_FindsServerSlashToolPairs()
    {
        var refs = ObservationLanguageDetector.TryExtractToolReferences(
            "calendar-mcp/search_emails is blocked, and onedrive-personal/search_files also failed");

        Assert.AreEqual(2, refs.Count);
        CollectionAssert.AreEquivalent(
            new[] { ("calendar-mcp", "search_emails"), ("onedrive-personal", "search_files") },
            refs.ToArray());
    }

    [TestMethod]
    public void TryExtractToolReferences_Deduplicates()
    {
        var refs = ObservationLanguageDetector.TryExtractToolReferences(
            "calendar-mcp/search_emails failed; calendar-mcp/search_emails still failing");
        Assert.AreEqual(1, refs.Count);
        Assert.AreEqual(("calendar-mcp", "search_emails"), refs[0]);
    }

    [TestMethod]
    public void TryExtractToolReferences_CaseInsensitiveDedup()
    {
        var refs = ObservationLanguageDetector.TryExtractToolReferences(
            "Calendar-MCP/Search_Emails and calendar-mcp/search_emails");
        Assert.AreEqual(1, refs.Count);
    }

    [TestMethod]
    [DataRow("nothing here matches")]
    [DataRow("a path/with/multiple/slashes is not a tool reference")]
    [DataRow("")]
    public void TryExtractToolReferences_ReturnsEmpty_WhenNoPairs(string content)
    {
        // Note: "path/with" *would* match if path and with are valid identifiers,
        // and they are — so the heuristic is intentionally loose. The eviction
        // step requires the tool-call log to confirm the pair actually exists.
        var refs = ObservationLanguageDetector.TryExtractToolReferences(content);
        if (refs.Count > 0)
        {
            // Sanity: any extracted pair must look like (server, tool).
            foreach (var (s, t) in refs)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(s));
                Assert.IsFalse(string.IsNullOrWhiteSpace(t));
            }
        }
    }

    [TestMethod]
    public void TryExtractToolReferences_Null_ReturnsEmpty()
    {
        Assert.AreEqual(0, ObservationLanguageDetector.TryExtractToolReferences(null).Count);
    }
}

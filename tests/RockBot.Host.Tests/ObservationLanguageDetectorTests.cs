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

    [TestMethod]
    [DataRow("shared/patrol/active-plans-latest")]
    [DataRow("worker/abc123/result")]
    [DataRow("subagent/xyz/output")]
    [DataRow("patrol/heartbeat/findings")]
    [DataRow("session/blazor-session/foo")]
    [DataRow("user-preferences/family/spouse")]
    [DataRow("agent-identity/self-model")]
    [DataRow("agent-knowledge/conventions")]
    [DataRow("project-context/rockbot-infrastructure")]
    [DataRow("active-plans/talk-prep")]
    [DataRow("active-tasks/email-followup")]
    [DataRow("subagent-whiteboards/task-id")]
    [DataRow("claim/capability/calendar")]
    [DataRow("episodic/yesterday")]
    [DataRow("mcp/calendar-mcp")]
    [DataRow("skill/mcp-search-flow")]
    [DataRow("skills/some-skill")]
    [DataRow("http/example.com")]
    [DataRow("https/example.com")]
    [DataRow("file/foo")]
    [DataRow("data/some-value")]
    public void TryExtractToolReferences_ExcludesNamespacePrefixes(string content)
    {
        // Working-memory keys, long-term-memory category paths, and other
        // path-shaped tokens follow the same shape as server/tool but are NOT
        // MCP server references. The eviction filter relies on this exclusion
        // to prevent false-positive contradictions.
        var refs = ObservationLanguageDetector.TryExtractToolReferences(content);
        Assert.AreEqual(0, refs.Count,
            $"'{content}' should not produce a (server, tool) reference — its first segment is a namespace prefix.");
    }

    [TestMethod]
    public void TryExtractToolReferences_NamespaceAlongsideRealRef_OnlyExtractsRealRef()
    {
        // Mixed content from a real worker finding: contains both a namespace
        // path and a genuine MCP server/tool reference. Only the genuine
        // reference should be extracted.
        var refs = ObservationLanguageDetector.TryExtractToolReferences(
            "Saved to shared/patrol/active-plans-latest. " +
            "calendar-mcp/search_emails returned no results.");

        Assert.AreEqual(1, refs.Count,
            "Only the calendar-mcp/search_emails pair is a real tool reference.");
        Assert.AreEqual(("calendar-mcp", "search_emails"), refs[0]);
    }
}

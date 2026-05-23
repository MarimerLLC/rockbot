using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Tests for the per-tool-result cap added to fight subagent-loop context bloat.
/// The cap fires per-call (independent of the global watermark) so a single oversized
/// tool result is stashed and inline-elided before the model ever sees the full text.
/// </summary>
[TestClass]
public class AgentLoopRunnerCapToolResultTests
{
    private const string ElisionMarkerPrefix = "[content elided to fit context window";
    private const string LegacyMarker = "[truncated to fit context window]";

    [TestMethod]
    public async Task Cap_ResultSmallerThanMax_ReturnsUnchanged()
    {
        var wm = new TestWorkingMemory();
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        const string smallResult = "small";

        var result = await AgentLoopRunner.CapToolResultAsync(
            smallResult, "call-1", "fetch_url", wm, stashState,
            maxChars: 100, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger.Instance);

        Assert.AreSame(smallResult, result, "Below-threshold results must pass through identity-equal.");
        Assert.AreEqual(0, wm.WriteCount, "Nothing should be stashed when under the cap.");
        Assert.IsTrue(stashState.Registry.IsEmpty);
    }

    [TestMethod]
    public async Task Cap_ResultExactlyAtMax_ReturnsUnchanged()
    {
        var wm = new TestWorkingMemory();
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var atLimit = new string('x', 100);

        var result = await AgentLoopRunner.CapToolResultAsync(
            atLimit, "call-1", "fetch_url", wm, stashState,
            maxChars: 100, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger.Instance);

        Assert.AreSame(atLimit, result, "Result at exactly the cap must not be trimmed.");
        Assert.AreEqual(0, wm.WriteCount);
    }

    [TestMethod]
    public async Task Cap_OversizeResultWithCallId_StashesAndReturnsHeadElisionTail()
    {
        var wm = new TestWorkingMemory();
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        stashState.ArgsSummaries["call-1"] = "url=https://example.com";

        // 4000 chars with distinctive head and tail sentinels so we can prove both survive.
        var bigResult = "HEAD-MARKER-START" + new string('A', 4000) + "TAIL-MARKER-END";

        var result = await AgentLoopRunner.CapToolResultAsync(
            bigResult, "call-1", "fetch_url", wm, stashState,
            maxChars: 500, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger.Instance);

        Assert.AreNotSame(bigResult, result);
        Assert.IsTrue(result.Length <= 600,
            $"Capped result should fit roughly within the cap (got {result.Length}, cap 500).");
        StringAssert.Contains(result, ElisionMarkerPrefix,
            "Capped result must include the elision marker between head and tail.");
        StringAssert.Contains(result, "id=call-1",
            "Elision marker must label the result with its callId so the registry can map it back.");
        StringAssert.Contains(result, "HEAD-MARKER-START",
            "Head of the original must be preserved by the head+tail cap.");
        StringAssert.Contains(result, "TAIL-MARKER-END",
            "Tail of the original must be preserved (the legacy head-only trim would discard it).");

        var stashKey = AgentLoopRunner.BuildStashKey("sess-1", "call-1");
        Assert.AreEqual(bigResult, wm.Get(stashKey),
            "Full original must be stashed in working memory at the namespaced stash key.");

        Assert.AreEqual(1, stashState.Registry.Snapshot().Count);
        var entry = stashState.Registry.Snapshot()[0];
        Assert.AreEqual("call-1", entry.CallId);
        Assert.AreEqual("fetch_url", entry.ToolName);
        Assert.AreEqual("url=https://example.com", entry.ArgsSummary);
        Assert.AreEqual(stashKey, entry.Key);
    }

    [TestMethod]
    public async Task Cap_OversizeResultWithoutCallId_FallsBackToLegacyHeadOnlyTruncation()
    {
        var wm = new TestWorkingMemory();
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var bigResult = new string('B', 4000);

        var result = await AgentLoopRunner.CapToolResultAsync(
            bigResult, callId: "", toolName: "parse_text", workingMemory: wm,
            stashState: stashState, maxChars: 500, headRatio: 0.6,
            ttl: TimeSpan.FromMinutes(60), logger: NullLogger.Instance);

        StringAssert.Contains(result, LegacyMarker,
            "Without a callId the cap falls back to legacy head-only truncation — the model " +
            "cannot recover from the registry, so we do not promise a recoverable elision.");
        Assert.IsFalse(result.Contains(ElisionMarkerPrefix),
            "No elision marker for missing callId — the registry has nothing to point at.");
        Assert.IsTrue(stashState.Registry.IsEmpty);
        Assert.AreEqual(0, wm.Count);
    }

    [TestMethod]
    public async Task Cap_OversizeResultWithoutStashState_FallsBackToLegacyHeadOnlyTruncation()
    {
        var wm = new TestWorkingMemory();
        var bigResult = new string('C', 4000);

        var result = await AgentLoopRunner.CapToolResultAsync(
            bigResult, "call-1", "fetch_url", wm, stashState: null,
            maxChars: 500, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger.Instance);

        StringAssert.Contains(result, LegacyMarker);
        Assert.AreEqual(0, wm.Count,
            "No stash state → no working-memory write (nothing to register against).");
    }

    [TestMethod]
    public async Task Cap_MaxCharsZero_DisablesCappingAndReturnsUnchanged()
    {
        var wm = new TestWorkingMemory();
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var bigResult = new string('D', 100_000);

        var result = await AgentLoopRunner.CapToolResultAsync(
            bigResult, "call-1", "fetch_url", wm, stashState,
            maxChars: 0, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger.Instance);

        Assert.AreSame(bigResult, result, "maxChars=0 disables the cap entirely.");
        Assert.AreEqual(0, wm.WriteCount);
        Assert.IsTrue(stashState.Registry.IsEmpty);
    }

    [TestMethod]
    public async Task Cap_SameCallIdTwice_DoesNotReStash()
    {
        var wm = new TestWorkingMemory();
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var bigResult = new string('E', 4000);

        await AgentLoopRunner.CapToolResultAsync(
            bigResult, "call-1", "fetch_url", wm, stashState,
            maxChars: 500, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger.Instance);
        Assert.AreEqual(1, wm.WriteCount);
        Assert.AreEqual(1, stashState.Registry.Snapshot().Count);

        // Re-cap the same callId. The registry already contains the entry, so the stash
        // write must not repeat — idempotency matches TrimLargeToolResultsAsync.
        await AgentLoopRunner.CapToolResultAsync(
            bigResult, "call-1", "fetch_url", wm, stashState,
            maxChars: 500, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger.Instance);

        Assert.AreEqual(1, wm.WriteCount,
            "Working memory must not be re-written when the callId is already registered.");
        Assert.AreEqual(1, stashState.Registry.Snapshot().Count,
            "Registry must remain idempotent on duplicate callId.");
    }

    [TestMethod]
    public async Task Cap_MissingArgsSummary_UsesPlaceholder()
    {
        var wm = new TestWorkingMemory();
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        // No ArgsSummaries entry for "call-1" — capture happens at dispatch time, but
        // a test/race could call CapToolResultAsync without one. The registry must still
        // accept the entry with a placeholder rather than crash.
        var bigResult = new string('F', 4000);

        await AgentLoopRunner.CapToolResultAsync(
            bigResult, "call-1", "fetch_url", wm, stashState,
            maxChars: 500, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger.Instance);

        var entry = stashState.Registry.Snapshot()[0];
        Assert.AreEqual("(args unavailable)", entry.ArgsSummary);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class TestWorkingMemory : IWorkingMemory
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
        public int WriteCount { get; private set; }
        public int Count => _entries.Count;
        public string? Get(string key) => _entries.TryGetValue(key, out var v) ? v : null;

        public Task SetAsync(string key, string value, TimeSpan? ttl = null,
            string? category = null, IReadOnlyList<string>? tags = null)
        {
            _entries[key] = value;
            WriteCount++;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key) =>
            Task.FromResult(_entries.TryGetValue(key, out var v) ? v : null);

        public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

        public Task DeleteAsync(string key)
        {
            _entries.Remove(key);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string? prefix = null)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(
            MemorySearchCriteria criteria, string? prefix = null) =>
            Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    }
}

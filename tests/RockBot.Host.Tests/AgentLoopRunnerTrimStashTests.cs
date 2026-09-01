using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Behavioural tests for the head+tail+stash trim path added under issue #337:
/// overflow-trimmed tool results survive as head + elision marker + tail, the full
/// original is stashed in working memory, and the system stash registry renders a
/// trusted retrieval pointer.
/// </summary>
[TestClass]
public class AgentLoopRunnerTrimStashTests
{
    private const string ElisionMarkerPrefix = "[content elided to fit context window";
    private const string LegacyMarker = "[truncated to fit context window]";
    private const string StashRegistryMarker = "[stash-registry]";

    [TestMethod]
    public async Task Trim_OversizeResultWithCallId_StashesOriginalAndRewritesWithHeadAndTail()
    {
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        stashState.ArgsSummaries["call-1"] = "url=https://example.com";

        var bigResult = new string('A', 4000) + "TAIL-MARKER-END";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "do the thing"),
            BuildAssistantWithCall("fetch_url", "call-1"),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", bigResult)]),
        };

        // Tiny budget forces a trim. 200 tokens × 4 chars/token × 0.9 ≈ 720 chars budget.
        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 200, "sess-1", stashState);

        var frc = (FunctionResultContent)messages[3].Contents[0];
        var trimmed = frc.Result?.ToString() ?? string.Empty;

        StringAssert.Contains(trimmed, ElisionMarkerPrefix,
            "Trimmed message must include the elision marker between head and tail.");
        StringAssert.Contains(trimmed, "id=call-1",
            "Elision marker must label the trimmed result with its callId.");
        StringAssert.Contains(trimmed, "TAIL-MARKER-END",
            "Tail of the original must be preserved by the head+tail trim (the head-only " +
            "predecessor would discard it).");
        Assert.IsFalse(trimmed.Contains(LegacyMarker),
            "Legacy '[truncated to fit context window]' marker must not appear for calls with a callId.");

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
    public async Task Trim_ResultWithoutCallId_FallsBackToLegacyHeadOnlyMarker()
    {
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };

        var bigResult = new string('B', 4000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "do the thing"),
            new(ChatRole.Tool, [new FunctionResultContent(callId: string.Empty, bigResult)]),
        };

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 200, "sess-1", stashState);

        var frc = (FunctionResultContent)messages[2].Contents[0];
        var trimmed = frc.Result?.ToString() ?? string.Empty;

        StringAssert.Contains(trimmed, LegacyMarker,
            "Without a callId the model cannot retrieve a stashed entry, so the trim falls " +
            "back to the legacy head-only marker rather than promising a recoverable elision.");
        Assert.IsFalse(trimmed.Contains(ElisionMarkerPrefix),
            "No elision marker for missing callId — the registry has nothing to point at.");
        Assert.IsTrue(stashState.Registry.IsEmpty,
            "Nothing should be added to the registry for trims that cannot be stashed.");
        Assert.AreEqual(0, wm.Count, "Nothing should be written to working memory either.");
    }

    [TestMethod]
    public async Task Trim_SameCallIdTwice_DoesNotReStash()
    {
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };

        var bigResult = new string('C', 4000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "do the thing"),
            BuildAssistantWithCall("fetch_url", "call-1"),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", bigResult)]),
        };

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 200, "sess-1", stashState);
        var firstWrites = wm.WriteCount;
        Assert.AreEqual(1, firstWrites);

        // Re-trim the same message with the same callId. The registry should already
        // contain the entry, so no second stash write.
        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 150, "sess-1", stashState);

        Assert.AreEqual(1, stashState.Registry.Snapshot().Count,
            "Registry must remain idempotent on duplicate callId.");
        Assert.AreEqual(firstWrites, wm.WriteCount,
            "Working memory must not be re-written when the callId is already registered.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_NonToolContentExceedsBudget_TerminatesInsteadOfSpinning()
    {
        // Regression for the 2026-06-05 runaway: when the non-tool content (here a huge
        // system message) alone exceeds the char budget and every tool result is small
        // enough to sit at the elision floor, the old `while(true)` could never reach the
        // budget and re-trimmed the same floor-sized results forever (400k+ "Trimmed tool
        // result" log lines, CPU pegged). The [Timeout] makes a re-introduced infinite
        // loop fail rather than hang the suite.
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };

        var messages = new List<ChatMessage>
        {
            // Non-trimmable: a system message far larger than any budget the trim targets.
            new(ChatRole.System, new string('S', 50_000)),
            new(ChatRole.User, "do the thing"),
            BuildAssistantWithCall("fetch_url", "call-1"),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", new string('A', 2_000))]),
        };

        // Tiny budget (≈720 chars) that the system message alone blows past — unreachable
        // by trimming tool results. The call must return rather than spin.
        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 200, "sess-1", stashState);

        // The tool result should have been trimmed at least once (down toward the floor),
        // and the message list must not have grown.
        Assert.AreEqual(4, messages.Count, "Trim must not add or remove messages.");
        var frc = (FunctionResultContent)messages[3].Contents[0];
        Assert.IsTrue((frc.Result?.ToString()?.Length ?? 0) <= 2_000,
            "The tool result must not have grown past its original size.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_NoToolResultsButOverBudget_TerminatesImmediately()
    {
        // Only non-tool content over budget and nothing to trim — must break out at once.
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, new string('S', 50_000)),
            new(ChatRole.User, "do the thing"),
        };

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 200, "sess-1", stashState);

        Assert.AreEqual(2, messages.Count);
    }

    [TestMethod]
    public void RefreshStashRegistryContext_InsertsSystemMessageWithKey()
    {
        var registry = new ToolResultStashRegistry();
        registry.Add(new ToolResultStashRegistry.Entry(
            "call-1", "fetch_url", "url=https://example.com", "stash/sess-1/call-1"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "preamble"),
            new(ChatRole.User, "do the thing"),
        };

        AgentLoopRunner.RefreshStashRegistryContext(messages, registry);

        var stashMsg = messages.FirstOrDefault(m =>
            m.Role == ChatRole.System &&
            m.Text?.StartsWith(StashRegistryMarker, StringComparison.Ordinal) == true);
        Assert.IsNotNull(stashMsg, "A system message starting with the stash registry marker must be inserted.");
        StringAssert.Contains(stashMsg!.Text!, "id=call-1");
        StringAssert.Contains(stashMsg!.Text!, "tool=fetch_url");
        StringAssert.Contains(stashMsg!.Text!, "key=stash/sess-1/call-1");
        StringAssert.Contains(stashMsg!.Text!, "get_from_working_memory",
            "Message must instruct the model how to retrieve the elided content.");
    }

    [TestMethod]
    public void RefreshStashRegistryContext_EmptyRegistry_RemovesExistingMessage()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "preamble"),
            new(ChatRole.System, $"{StashRegistryMarker} stale content"),
            new(ChatRole.User, "do the thing"),
        };

        AgentLoopRunner.RefreshStashRegistryContext(messages, new ToolResultStashRegistry());

        Assert.IsFalse(messages.Any(m =>
            m.Text?.StartsWith(StashRegistryMarker, StringComparison.Ordinal) == true),
            "Empty registry must clear the existing stash-registry system message.");
    }

    [TestMethod]
    public void RefreshStashRegistryContext_ExistingMessage_IsReplacedNotDuplicated()
    {
        var registry = new ToolResultStashRegistry();
        registry.Add(new ToolResultStashRegistry.Entry("c1", "t", "args", "stash/_/c1"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "preamble"),
            new(ChatRole.System, $"{StashRegistryMarker} stale"),
            new(ChatRole.User, "do it"),
        };

        AgentLoopRunner.RefreshStashRegistryContext(messages, registry);
        AgentLoopRunner.RefreshStashRegistryContext(messages, registry);

        var count = messages.Count(m =>
            m.Role == ChatRole.System &&
            m.Text?.StartsWith(StashRegistryMarker, StringComparison.Ordinal) == true);
        Assert.AreEqual(1, count, "Refresh must replace in place, never duplicate.");
    }

    [TestMethod]
    public void BuildStashKey_NullSession_UsesPlaceholderNamespace()
    {
        Assert.AreEqual("stash/_/abc", AgentLoopRunner.BuildStashKey(null, "abc"));
        Assert.AreEqual("stash/_/abc", AgentLoopRunner.BuildStashKey("", "abc"));
        Assert.AreEqual("stash/sess-1/abc", AgentLoopRunner.BuildStashKey("sess-1", "abc"));
    }

    [TestMethod]
    public void TruncateArgsSummary_LongerThanLimit_AppendsEllipsis()
    {
        var s = new string('x', 250);
        var truncated = AgentLoopRunner.TruncateArgsSummary(s);
        Assert.AreEqual(201, truncated.Length);
        Assert.IsTrue(truncated.EndsWith('…'));
    }

    [TestMethod]
    public void TruncateArgsSummary_WithinLimit_ReturnsAsIs()
    {
        const string s = "url=https://example.com";
        Assert.AreEqual(s, AgentLoopRunner.TruncateArgsSummary(s));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_OversizeRetrievalResult_IsNotReStashedAndDoesNotSpin()
    {
        // Regression for the 2026-06-10 communications-briefing runaway: an explicit
        // GetFromWorkingMemory retrieval returned an oversized result, which the trim
        // re-stashed under the *retrieval* call's id and advertised back to the model.
        // The model re-fetched the new key, got a larger reference, which was re-stashed
        // again — a retrieve→re-stash→retrieve loop that burned the whole subagent budget.
        // Explicit working-memory reads must be left intact.
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };

        var bigRetrieval = new string('R', 4000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "do the thing"),
            BuildAssistantWithCall("get_from_working_memory", "call-1"),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", bigRetrieval)]),
        };

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 200, "sess-1", stashState);

        var frc = (FunctionResultContent)messages[3].Contents[0];
        Assert.AreEqual(bigRetrieval, frc.Result?.ToString(),
            "An explicit GetFromWorkingMemory retrieval must be left intact, not head+tail trimmed.");
        Assert.IsTrue(stashState.Registry.IsEmpty,
            "A retrieval result must never be re-stashed (that mints a fresh key and loops the model).");
        Assert.AreEqual(0, wm.WriteCount, "Nothing should be written to working memory for a retrieval result.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_RetrievalAndNormalResult_TrimsNormalAndSkipsRetrieval()
    {
        // When both an exempt retrieval and a normal oversized result are over budget,
        // the trim must skip the retrieval and reclaim space from the normal result.
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };

        var bigRetrieval = new string('R', 4000);
        var biggerNormal = new string('N', 5000) + "NORMAL-TAIL";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "do the thing"),
            BuildAssistantWithCall("get_from_working_memory", "call-ret"),
            new(ChatRole.Tool, [new FunctionResultContent("call-ret", bigRetrieval)]),
            BuildAssistantWithCall("fetch_url", "call-norm"),
            new(ChatRole.Tool, [new FunctionResultContent("call-norm", biggerNormal)]),
        };

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 200, "sess-1", stashState);

        var retrieval = (FunctionResultContent)messages[3].Contents[0];
        Assert.AreEqual(bigRetrieval, retrieval.Result?.ToString(),
            "The retrieval result must be untouched.");

        var normal = (FunctionResultContent)messages[5].Contents[0];
        StringAssert.Contains(normal.Result?.ToString() ?? string.Empty, ElisionMarkerPrefix,
            "The normal result must be head+tail trimmed to reclaim space.");

        Assert.AreEqual(1, stashState.Registry.Snapshot().Count,
            "Only the normal result should be stashed.");
        Assert.AreEqual("call-norm", stashState.Registry.Snapshot()[0].CallId);
    }

    [TestMethod]
    public async Task CapToolResult_RetrievalTool_ReturnsUnchangedWithoutStashing()
    {
        var wm = new TestWorkingMemory();
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var big = new string('R', 4000);

        var capped = await AgentLoopRunner.CapToolResultAsync(
            big, callId: "call-1", toolName: "get_from_working_memory",
            workingMemory: wm, stashState: stashState,
            maxChars: 1000, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger<AgentLoopRunner>.Instance);

        Assert.AreEqual(big, capped, "An explicit retrieval must be returned in full, not capped.");
        Assert.IsTrue(stashState.Registry.IsEmpty, "A retrieval result must not be stashed.");
        Assert.AreEqual(0, wm.WriteCount, "A retrieval result must not be written back to working memory.");
    }

    [TestMethod]
    public async Task CapToolResult_NormalTool_CapsAndStashes()
    {
        var wm = new TestWorkingMemory();
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var big = new string('N', 4000);

        var capped = await AgentLoopRunner.CapToolResultAsync(
            big, callId: "call-1", toolName: "fetch_url",
            workingMemory: wm, stashState: stashState,
            maxChars: 1000, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger<AgentLoopRunner>.Instance);

        Assert.IsTrue(capped.Length < big.Length, "A normal oversized result must be capped.");
        StringAssert.Contains(capped, ElisionMarkerPrefix);
        Assert.AreEqual(1, stashState.Registry.Snapshot().Count, "A normal capped result must be stashed.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AgentLoopRunner NewRunner(IWorkingMemory workingMemory)
    {
        var options = Options.Create(new AgentHostOptions
        {
            ToolResultStashTtlMinutes = 60,
            ToolResultStashHeadTailRatio = 0.6,
        });

        // The trim path uses workingMemory, hostOptions, and logger only. The other
        // primary-ctor parameters are not exercised by these tests, so passing null!
        // is safe; if someone routes a different code path through the trim helper,
        // it'll NRE loudly here and we'll know to widen the construction.
        return new AgentLoopRunner(
            llmClient: null!,
            workingMemory: workingMemory,
            modelBehavior: null!,
            feedbackStore: null!,
            clock: null!,
            hostOptions: options,
            skillStore: null!,
            serviceSearchIndexProviders: Array.Empty<IServiceSearchIndex>(),
            conversationMemory: null!,
            logger: NullLogger<AgentLoopRunner>.Instance);
    }

    private static ChatMessage BuildAssistantWithCall(string toolName, string callId) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, toolName)]);

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

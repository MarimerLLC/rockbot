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
        StringAssert.Contains(stashMsg!.Text!, "GetFromWorkingMemory",
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

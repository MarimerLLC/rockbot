using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// file_read pages its own output, so the generic per-result cap and chunking must leave it
/// alone, and the watermark trim must reach it only as a last resort (issue #664).
/// </summary>
[TestClass]
public class SelfPagingToolsTests
{
    private const string ElisionMarkerPrefix = "[content elided to fit context window";

    [TestMethod]
    public void FileRead_IsSelfPaging_ButNotStashExempt()
    {
        Assert.IsTrue(SelfPagingTools.Contains("file_read"));
        Assert.IsTrue(SelfPagingTools.Contains("FILE_READ"));
        Assert.IsFalse(SelfPagingTools.Contains("file_write"));
        Assert.IsFalse(SelfPagingTools.Contains(null));
        Assert.IsFalse(StashExemptTools.Contains("file_read"),
            "file_read must stay eligible for the watermark trim, so it is not in the stash-exempt set.");
    }

    [TestMethod]
    public async Task CapToolResult_12KbFileRead_IsReturnedInFull()
    {
        // The production numbers from #664: an 11.5 KB deck against the 8,000-char default cap.
        var wm = new TestWorkingMemory();
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var deck = string.Concat(Enumerable.Repeat("---\n# Slide\n- point about MCP v2\n\n", 400));
        Assert.IsTrue(deck.Length > 12_000);

        var capped = await AgentLoopRunner.CapToolResultAsync(
            deck, callId: "call-1", toolName: "file_read",
            workingMemory: wm, stashState: stashState,
            maxChars: new AgentHostOptions().ToolResultMaxChars, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger<AgentLoopRunner>.Instance);

        Assert.AreSame(deck, capped, "file_read must not be head+tail capped.");
        Assert.IsTrue(stashState.Registry.IsEmpty);
        Assert.AreEqual(0, wm.WriteCount);
    }

    [TestMethod]
    public async Task CapToolResult_TextLoopLegacyMode_FileReadIsReturnedInFull()
    {
        // The text-based loop calls the cap with no callId (legacy head-only mode).
        var wm = new TestWorkingMemory();
        var big = new string('F', 20_000);

        var capped = await AgentLoopRunner.CapToolResultAsync(
            big, callId: null, toolName: "file_read",
            workingMemory: wm, stashState: null,
            maxChars: 8_000, headRatio: 0.6, ttl: TimeSpan.FromMinutes(60),
            logger: NullLogger<AgentLoopRunner>.Instance);

        Assert.AreSame(big, capped);
    }

    [TestMethod]
    public async Task Chunking_FileReadResult_IsNotChunked()
    {
        var wm = new TestWorkingMemory();
        var big = new string('C', 5_000);
        var inner = AIFunctionFactory.Create(() => big, "file_read");
        var chunking = new ChunkingAIFunction(inner, wm, "session/x", chunkingThreshold: 1_000,
            NullLogger.Instance);

        var result = await chunking.InvokeAsync(new AIFunctionArguments());

        Assert.AreEqual(big, result?.ToString());
        Assert.AreEqual(0, wm.WriteCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_PrefersOtherResultsOverFileRead()
    {
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };

        var fileRead = new string('F', 6_000);
        var other = new string('N', 4_000) + "NORMAL-TAIL";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "edit the deck"),
            Call("fetch_url", "call-norm"),
            new(ChatRole.Tool, [new FunctionResultContent("call-norm", other)]),
            Call("file_read", "call-read"),
            new(ChatRole.Tool, [new FunctionResultContent("call-read", fileRead)]),
        };

        // Budget ≈ 2,000 tokens × 4 × 0.9 = 7,200 chars: trimming the other result alone fits.
        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 2_000, "sess-1", stashState);

        var read = (FunctionResultContent)messages[5].Contents[0];
        Assert.AreEqual(fileRead, read.Result?.ToString(),
            "The larger file_read must survive while another result can still give up space.");
        var normal = (FunctionResultContent)messages[3].Contents[0];
        StringAssert.Contains(normal.Result?.ToString() ?? string.Empty, ElisionMarkerPrefix);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_FileReadIsTrimmedAsLastResort()
    {
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };

        var fileRead = new string('F', 6_000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "edit the deck"),
            Call("file_read", "call-read"),
            new(ChatRole.Tool, [new FunctionResultContent("call-read", fileRead)]),
        };

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 200, "sess-1", stashState);

        var read = (FunctionResultContent)messages[3].Contents[0];
        StringAssert.Contains(read.Result?.ToString() ?? string.Empty, ElisionMarkerPrefix,
            "With nothing else to trim, the watermark backstop still applies to file_read.");
    }

    private static ChatMessage Call(string toolName, string callId) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, toolName)]);

    private static AgentLoopRunner NewRunner(IWorkingMemory workingMemory)
    {
        var options = Options.Create(new AgentHostOptions
        {
            ToolResultStashTtlMinutes = 60,
            ToolResultStashHeadTailRatio = 0.6,
        });

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

    private sealed class TestWorkingMemory : IWorkingMemory
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
        public int WriteCount { get; private set; }

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

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Tests for the per-content context-size estimate (issue #564). Before this, the estimate
/// knew only <see cref="TextContent"/> and <see cref="FunctionResultContent"/> and charged
/// everything else a flat 50 chars — so a 1.8 MB image counted as 50 characters and was
/// invisible to every context-pressure decision in the loop. These tests pin both the
/// per-content sizing and the fact that an image actually drives the trim path.
/// </summary>
[TestClass]
public class AgentLoopRunnerEstimateContentCharsTests
{
    private const string ElisionMarkerPrefix = "[content elided to fit context window";

    // ── Per-content sizing ───────────────────────────────────────────────────

    [TestMethod]
    public void Estimate_LargeImage_IsCappedNotCountedAsFifty()
    {
        // The regression this issue is titled for: 1.8 MB of image bytes used to count as 50.
        var image = new DataContent(new byte[1_800_000], "image/png");

        var chars = AgentLoopRunner.EstimateContentChars(image);

        Assert.AreEqual(AgentLoopRunner.MaxImageChars, chars,
            "A large image must be charged the image ceiling — providers downscale to a fixed " +
            "tile budget, so neither 50 chars nor the raw 2.4M base64 chars is honest.");
        Assert.AreNotEqual(AgentLoopRunner.UnknownContentChars, chars,
            "An image must no longer fall through to the unknown-content placeholder.");
    }

    [TestMethod]
    public void Estimate_SmallImage_IsSizedByBase64LengthBelowTheCap()
    {
        const int Bytes = 3_000;
        var image = new DataContent(new byte[Bytes], "image/jpeg");

        var chars = AgentLoopRunner.EstimateContentChars(image);

        Assert.AreEqual(Bytes / 3 * 4, chars,
            "Under the ceiling an image is charged its base64 wire cost (4 chars per 3 bytes).");
        Assert.IsTrue(chars < AgentLoopRunner.MaxImageChars,
            "This fixture is only meaningful if it stays below the cap.");
    }

    [TestMethod]
    public void Estimate_NonImageData_IsSizedByFullBase64LengthUncapped()
    {
        const int Bytes = 900_000;
        var pdf = new DataContent(new byte[Bytes], "application/pdf");

        var chars = AgentLoopRunner.EstimateContentChars(pdf);

        Assert.AreEqual(Bytes / 3 * 4, chars,
            "Only images get the tile-budget ceiling; other media ride as base64 and cost " +
            "proportionally to their encoded length.");
        Assert.IsTrue(chars > AgentLoopRunner.MaxImageChars,
            "The image cap must not be applied to non-image media.");
    }

    [TestMethod]
    public void Estimate_ImageWithNoPayload_ChargesTheCeilingNotZero()
    {
        // A degenerate image part is a malformed request, not a free one. Charging zero would
        // be the same silent under-count in a new disguise.
        var image = new DataContent(ReadOnlyMemory<byte>.Empty, "image/png");

        var chars = AgentLoopRunner.EstimateContentChars(image);

        Assert.AreEqual(AgentLoopRunner.MaxImageChars, chars);
    }

    [TestMethod]
    public void Estimate_FunctionCall_IsSizedByItsArgumentsNotAFlatConstant()
    {
        // Tool-call arguments can be a whole wisp script — this arm is live today, unlike
        // the image arms.
        var script = new string('x', 5_000);
        var call = new FunctionCallContent(
            "call-1", "run_wisp", new Dictionary<string, object?> { ["script"] = script });

        var chars = AgentLoopRunner.EstimateContentChars(call);

        Assert.IsTrue(chars >= script.Length,
            $"A tool call carrying a {script.Length:N0}-char argument must be counted by its " +
            $"arguments, not by a flat placeholder (got {chars}).");
    }

    [TestMethod]
    public void Estimate_FunctionCallWithoutArguments_IsSizedByName()
    {
        var call = new FunctionCallContent("call-1", "list_memories");

        Assert.AreEqual("list_memories".Length, AgentLoopRunner.EstimateContentChars(call));
    }

    [TestMethod]
    public void Estimate_TextReasoningContent_IsSizedByItsText()
    {
        // TextReasoningContent does not derive from TextContent, so it needed its own arm.
        var reasoning = new TextReasoningContent(new string('r', 1_234));

        Assert.AreEqual(1_234, AgentLoopRunner.EstimateContentChars(reasoning));
    }

    [TestMethod]
    public void Estimate_UriContent_IsSizedByTheUriString()
    {
        const string Url = "https://example.com/a/rather/long/path/to/an/asset.png";
        var uri = new UriContent(Url, "image/png");

        Assert.AreEqual(Url.Length, AgentLoopRunner.EstimateContentChars(uri));
    }

    [TestMethod]
    public void Estimate_ErrorContent_IsSizedByItsStrings()
    {
        var error = new ErrorContent(new string('m', 100))
        {
            ErrorCode = new string('c', 10),
            Details = new string('d', 200),
        };

        Assert.AreEqual(310, AgentLoopRunner.EstimateContentChars(error));
    }

    [TestMethod]
    public void Estimate_TextAndFunctionResult_KeepTheirOriginalSizing()
    {
        Assert.AreEqual(42, AgentLoopRunner.EstimateContentChars(new TextContent(new string('t', 42))));
        Assert.AreEqual(
            17,
            AgentLoopRunner.EstimateContentChars(new FunctionResultContent("c1", new string('r', 17))));
    }

    [TestMethod]
    public void Estimate_UnknownContentType_FallsBackToThePlaceholder()
    {
        Assert.AreEqual(
            AgentLoopRunner.UnknownContentChars,
            AgentLoopRunner.EstimateContentChars(new UnmodelledContent()));
    }

    [TestMethod]
    public void EstimateMessageChars_SumsEveryPart()
    {
        var m = new ChatMessage(ChatRole.User,
        [
            new TextContent(new string('t', 100)),
            new DataContent(new byte[1_800_000], "image/png"),
        ]);

        Assert.AreEqual(100 + AgentLoopRunner.MaxImageChars, AgentLoopRunner.EstimateMessageChars(m));
    }

    // ── Trim-path coverage ───────────────────────────────────────────────────

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_ImageOnUserMessage_PushesContextOverBudgetAndTrimsToolResult()
    {
        // Measuring an image correctly in isolation is not the point — the point is that its
        // weight reaches the trim decision. With the old flat 50 this conversation looked
        // small enough to leave alone.
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };

        var toolResult = new string('A', 1_500) + "TAIL-MARKER-END";
        var messages = BuildConversation(toolResult, withImage: true);

        // ≈2,880-char budget: the text alone (~1,600 chars) fits, the image does not.
        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 800, "sess-1", stashState);

        var frc = (FunctionResultContent)messages[^1].Contents[0];
        var trimmed = frc.Result?.ToString() ?? string.Empty;

        StringAssert.Contains(trimmed, ElisionMarkerPrefix,
            "The image's weight must push the total past the budget and make the trim fire.");
        Assert.AreEqual(1, stashState.Registry.Snapshot().Count,
            "The trimmed tool result must be stashed so the model can retrieve it.");
        Assert.AreEqual(toolResult, wm.Get(AgentLoopRunner.BuildStashKey("sess-1", "call-1")));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_SameConversationWithoutTheImage_LeavesTheToolResultAlone()
    {
        // The control for the test above: identical fixture and budget, image removed. If this
        // also trimmed, the previous test would prove nothing about the image.
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };

        var toolResult = new string('A', 1_500) + "TAIL-MARKER-END";
        var messages = BuildConversation(toolResult, withImage: false);

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 800, "sess-1", stashState);

        var frc = (FunctionResultContent)messages[^1].Contents[0];
        Assert.AreEqual(toolResult, frc.Result?.ToString(),
            "Without the image this conversation is under budget and must not be trimmed.");
        Assert.IsTrue(stashState.Registry.IsEmpty);
        Assert.AreEqual(0, wm.WriteCount);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<ChatMessage> BuildConversation(string toolResult, bool withImage)
    {
        var userParts = new List<AIContent> { new TextContent("what is in this picture?") };
        if (withImage)
            userParts.Add(new DataContent(new byte[1_800_000], "image/png"));

        return
        [
            new ChatMessage(ChatRole.System, "system prompt"),
            new ChatMessage(ChatRole.User, userParts),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "fetch_url")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", toolResult)]),
        ];
    }

    private static AgentLoopRunner NewRunner(IWorkingMemory workingMemory)
    {
        var options = Options.Create(new AgentHostOptions
        {
            ToolResultStashTtlMinutes = 60,
            ToolResultStashHeadTailRatio = 0.6,
        });

        // Only workingMemory, hostOptions and logger are exercised by the trim path; the
        // remaining primary-ctor parameters would NRE loudly if a different path were routed
        // through here. Same shape as AgentLoopRunnerTrimStashTests.
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

    /// <summary>An AIContent subclass the estimate deliberately does not model.</summary>
    private sealed class UnmodelledContent : AIContent;

    private sealed class TestWorkingMemory : IWorkingMemory
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
        public int WriteCount { get; private set; }
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

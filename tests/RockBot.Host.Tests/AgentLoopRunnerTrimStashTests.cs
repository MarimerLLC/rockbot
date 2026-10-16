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
    public void AppendStashRegistryDelta_FirstEntry_AppendsNoteWithKeyAtTail()
    {
        var state = new AgentLoopStashContext.State { SessionId = "sess-1" };
        state.Registry.Add(new ToolResultStashRegistry.Entry(
            "call-1", "fetch_url", "url=https://example.com", "stash/sess-1/call-1"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "preamble"),
            new(ChatRole.User, "do the thing"),
        };

        AgentLoopRunner.AppendStashRegistryDelta(messages, state);

        Assert.AreEqual(3, messages.Count);
        var stashMsg = messages[^1];
        Assert.AreEqual(ChatRole.System, stashMsg.Role);
        Assert.IsTrue(stashMsg.Text!.StartsWith(StashRegistryMarker, StringComparison.Ordinal),
            "The note goes at the end of the conversation, not ahead of the user message (#710).");
        StringAssert.Contains(stashMsg.Text!, "id=call-1");
        StringAssert.Contains(stashMsg.Text!, "tool=fetch_url");
        StringAssert.Contains(stashMsg.Text!, "key=stash/sess-1/call-1");
        StringAssert.Contains(stashMsg.Text!, "get_from_working_memory",
            "Message must instruct the model how to retrieve the elided content.");
    }

    [TestMethod]
    public void AppendStashRegistryDelta_NoNewEntries_AppendsNothing()
    {
        var state = new AgentLoopStashContext.State();
        state.Registry.Add(new ToolResultStashRegistry.Entry("c1", "t", "args", "stash/_/c1"));
        var messages = new List<ChatMessage> { new(ChatRole.System, "preamble"), new(ChatRole.User, "do it") };

        AgentLoopRunner.AppendStashRegistryDelta(messages, state);
        AgentLoopRunner.AppendStashRegistryDelta(messages, state);
        AgentLoopRunner.AppendStashRegistryDelta(messages, new AgentLoopStashContext.State());

        Assert.AreEqual(1, messages.Count(m => m.Text?.StartsWith(StashRegistryMarker, StringComparison.Ordinal) == true),
            "An unchanged registry adds no further note; an empty one adds none at all.");
    }

    [TestMethod]
    public void AppendStashRegistryDelta_NewEntry_AppendsOnlyIt_AndLeavesTheEarlierNoteUntouched()
    {
        var state = new AgentLoopStashContext.State();
        state.Registry.Add(new ToolResultStashRegistry.Entry("c1", "t", "args", "stash/_/c1"));
        var messages = new List<ChatMessage> { new(ChatRole.System, "preamble"), new(ChatRole.User, "do it") };
        AgentLoopRunner.AppendStashRegistryDelta(messages, state);
        var firstNote = messages[^1];
        var firstText = firstNote.Text;
        messages.Add(new ChatMessage(ChatRole.Assistant, "working"));

        state.Registry.Add(new ToolResultStashRegistry.Entry("c2", "t", "args", "stash/_/c2"));
        AgentLoopRunner.AppendStashRegistryDelta(messages, state);

        Assert.AreSame(firstNote, messages[2], "The earlier note keeps its position and instance.");
        Assert.AreEqual(firstText, messages[2].Text, "The earlier note's text is not rewritten.");
        var delta = messages[^1].Text!;
        StringAssert.StartsWith(delta, StashRegistryMarker);
        StringAssert.Contains(delta, "key=stash/_/c2");
        Assert.IsFalse(delta.Contains("key=stash/_/c1"), "The delta lists only entries no earlier note listed.");
    }

    [TestMethod]
    public void TakeTaskListUpdate_RendersOnlyOnChange()
    {
        var taskList = new AgentTaskList();
        Assert.IsNull(ContextNotes.TakeTaskListUpdate(taskList), "An empty list that was never shown adds nothing.");

        taskList.CreateOrReplace(["first step"]);
        var first = ContextNotes.TakeTaskListUpdate(taskList);
        Assert.IsNotNull(first);
        StringAssert.Contains(first.Text, "first step");
        Assert.IsNull(ContextNotes.TakeTaskListUpdate(taskList), "An unchanged list adds no note.");

        taskList.CreateOrReplace(["first step", "second step"]);
        var second = ContextNotes.TakeTaskListUpdate(taskList);
        Assert.IsNotNull(second);
        StringAssert.Contains(second.Text, "second step");
        StringAssert.Contains(second.Text, "supersedes");
    }

    [TestMethod]
    public void SplicePlacedNotes_InsertsEachNoteAfterItsAnchor_InPlacementOrder()
    {
        var system = new ChatMessage(ChatRole.System, "preamble");
        var user = new ChatMessage(ChatRole.User, "do it");
        var call = BuildAssistantWithCall("fetch_url", "call-1");
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "ok")]);
        var reply = new ChatMessage(ChatRole.Assistant, "done");
        var noteA = new ChatMessage(ChatRole.System, "[stash-registry] a");
        var noteB = new ChatMessage(ChatRole.System, "[stash-registry] b");
        var noteC = new ChatMessage(ChatRole.System, "[stash-registry] c");

        var state = new AgentLoopStashContext.State();
        state.PlacedNotes.Add(new ContextNotes.Placed(user, noteA));
        state.PlacedNotes.Add(new ContextNotes.Placed(result, noteB));
        state.PlacedNotes.Add(new ContextNotes.Placed(result, noteC));
        var messages = new List<ChatMessage> { system, user, call, result, reply };

        AgentLoopRunner.SplicePlacedNotes(messages, state);

        CollectionAssert.AreEqual(new[] { system, user, noteA, call, result, noteB, noteC, reply }, messages);
        Assert.AreEqual(0, state.PlacedNotes.Count, "Spliced notes are forgotten.");
    }

    [TestMethod]
    public void ResolveToolResultStashWatermarkTokens_UsesTheTierOverride_ElseTheGlobalValue()
    {
        var options = new AgentHostOptions
        {
            ToolResultStashWatermarkTokens = 30_000,
            ToolResultStashWatermarkTokensByTier = { [ModelTier.High] = 120_000 },
        };

        Assert.AreEqual(120_000, options.ResolveToolResultStashWatermarkTokens(ModelTier.High));
        Assert.AreEqual(30_000, options.ResolveToolResultStashWatermarkTokens(ModelTier.Balanced));
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

        // ≈5,000-char budget: reachable by cutting the normal result alone.
        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 1_400, "sess-1", stashState);

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
    [Timeout(10_000)]
    public async Task Trim_OverBudget_CutsOldestFirstToTheTarget_AndSparesTheLatestBatch()
    {
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var messages = new List<ChatMessage> { new(ChatRole.System, "system prompt"), new(ChatRole.User, "go") };
        for (var i = 1; i <= 5; i++)
        {
            messages.Add(BuildAssistantWithCall("fetch_url", $"call-{i}"));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", new string((char)('a' + i), 3_000))]));
        }

        // Budget ~ 3,334 x 4 x 0.9 ~ 12,000 chars; the five results (~15,000) cross it. The target
        // is half the budget, so the trim keeps going well past the budget, oldest first.
        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 3_334, "sess-1", stashState);

        for (var i = 1; i <= 4; i++)
        {
            var older = ((FunctionResultContent)messages[2 * i + 1].Contents[0]).Result!.ToString()!;
            StringAssert.Contains(older, ElisionMarkerPrefix, $"call-{i} is older and gives up its space.");
            Assert.IsTrue(older.Length <= ToolResultTrimmer.DefaultFloorChars, $"call-{i} is cut to the floor, not just enough.");
        }
        var latest = ((FunctionResultContent)messages[^1].Contents[0]).Result!.ToString()!;
        Assert.AreEqual(3_000, latest.Length, "The latest batch is spared while older results can reach the target.");
        Assert.AreEqual(4, stashState.Registry.Snapshot().Count);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_AfterAStepDown_LeavesHistoryAloneUntilTheBudgetIsCrossedAgain()
    {
        // The point of the step down (#710): the iterations that follow append onto an
        // unchanged prefix, so the provider's prompt cache keeps hitting.
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var messages = new List<ChatMessage> { new(ChatRole.System, "system prompt"), new(ChatRole.User, "go") };
        var trims = 0;
        for (var i = 1; i <= 30; i++)
        {
            messages.Add(BuildAssistantWithCall("fetch_url", $"call-{i}"));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", new string('x', 1_500))]));

            var before = messages.SelectMany(m => m.Contents).ToList();
            await runner.TrimLargeToolResultsAsync(messages, maxTokens: 3_334, "sess-1", stashState);
            var after = messages.SelectMany(m => m.Contents).ToList();
            if (!before.SequenceEqual(after))
                trims++;
        }

        // The budget is first crossed around iteration 8; trimming every iteration from there
        // would be ~22 trims. Stepping down to half the budget leaves room for several appends.
        Assert.IsTrue(trims is > 0 and <= 7,
            $"Thirty 1,500-char results against a 12,000-char budget should trim in a few large steps, not {trims}.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_WhenUntrimmableContentKeepsItOverBudget_LeavesFloorSizedHistoryAlone()
    {
        // Live run, #710: dozens of working-memory retrievals (untrimmable then; since #713 only
        // those without a resolvable key, like this one) kept the context far over budget, so the target was out of reach and every trim dragged the oldest results from
        // the floor to 200 chars, rewriting the start of the loop and losing the whole cache.
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var messages = new List<ChatMessage> { new(ChatRole.System, "system prompt"), new(ChatRole.User, "go") };
        for (var i = 1; i <= 4; i++)
        {
            messages.Add(BuildAssistantWithCall("fetch_url", $"old-{i}"));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"old-{i}", new string('o', 950))]));
        }
        messages.Add(BuildAssistantWithCall("get_from_working_memory", "ret-1"));
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("ret-1", new string('r', 40_000))]));
        messages.Add(BuildAssistantWithCall("fetch_url", "new-1"));
        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("new-1", new string('n', 3_000))]));

        var olderBefore = messages.Take(10).SelectMany(m => m.Contents).ToList();

        // ~12,000-char budget, unreachable: the retrieval alone is 40,000.
        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 3_334, "sess-1", stashState);

        CollectionAssert.AreEqual(olderBefore, messages.Take(10).SelectMany(m => m.Contents).ToList(),
            "Results already at the floor stay as they are when cutting them further can't reach the goal.");
        Assert.AreEqual(3_000, ((FunctionResultContent)messages[^1].Contents[0]).Result!.ToString()!.Length,
            "The latest batch isn't cut for a budget it can't meet.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_OlderRetrievalOfStoredKey_IsCutWithPointerToTheSameKey_AndNotReStashed()
    {
        // #713: an older retrieval gives up its space like any other result, but the marker names
        // the key the model already read. No new key, no registry entry, no write.
        var wm = new TestWorkingMemory();
        const string key = "stash/sess-1/orig-call";
        wm.Seed(key, new string('R', 4000));
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "do the thing"),
            BuildAssistantWithCall("get_from_working_memory", "call-ret", key),
            new(ChatRole.Tool, [new FunctionResultContent("call-ret", new string('R', 4000) + "RET-TAIL")]),
            BuildAssistantWithCall("fetch_url", "call-new"),
            new(ChatRole.Tool, [new FunctionResultContent("call-new", "small")]),
        };

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 800, "sess-1", stashState);

        var retrieval = ((FunctionResultContent)messages[3].Contents[0]).Result!.ToString()!;
        StringAssert.Contains(retrieval, AgentLoopRunner.BuildRetrievalElisionMarker("call-ret"),
            "The marker must send the model back to its own call.");
        Assert.IsFalse(retrieval.Contains(key),
            "The marker carries no key: keys inside tool output are untrusted, and the key is in the model's own call.");
        StringAssert.Contains(retrieval, "RET-TAIL", "The cut keeps a head and a tail like any other trim.");
        Assert.IsFalse(retrieval.Contains(ElisionMarkerPrefix),
            "The retrieval must not be advertised as a stash entry under its own call id — that is the re-stash loop.");
        Assert.IsTrue(retrieval.Length < 4000);
        Assert.IsTrue(stashState.Registry.IsEmpty, "A retrieval is never registered as a new stash entry.");
        Assert.AreEqual(0, wm.WriteCount, "A retrieval is never written back to working memory.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_RetrievalWhoseMarkerExceedsTheFloor_IsCutToTheMarker_WhenThatReachesTheBudget()
    {
        // A long call id makes the marker longer than the 200-char hard floor. Cutting to the floor
        // must still be possible (to the marker alone), and the reachability check must not count
        // savings below the marker.
        var wm = new TestWorkingMemory();
        const string key = "stash/sess-1/orig-call";
        wm.Seed(key, "value");
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var callId = "call-" + new string('x', 400);
        var marker = AgentLoopRunner.BuildRetrievalElisionMarker(callId);
        Assert.IsTrue(marker.Length > 200, "Setup: the marker must be longer than the hard floor.");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "do the thing"),
            BuildAssistantWithCall("get_from_working_memory", callId, key),
            new(ChatRole.Tool, [new FunctionResultContent(callId, new string('R', 4000))]),
            BuildAssistantWithCall("fetch_url", "call-new"),
            new(ChatRole.Tool, [new FunctionResultContent("call-new", "small")]),
        };

        // Budget: reachable with the retrieval cut to its marker, not with it at the 1,000-char floor.
        var others = messages.Where((_, i) => i != 3).Sum(m => AgentLoopRunner.EstimateMessageChars(m));
        var maxTokens = (int)Math.Ceiling((others + marker.Length + 200) / 3.6);
        var charBudget = (int)(maxTokens * 4 * 0.9);
        Assert.IsTrue(others + 1_000 > charBudget && others + marker.Length <= charBudget, "Setup: budget window.");

        await runner.TrimLargeToolResultsAsync(messages, maxTokens, "sess-1", stashState);

        Assert.AreEqual(marker, ((FunctionResultContent)messages[3].Contents[0]).Result?.ToString(),
            "Below the marker's own length the retrieval is cut to the marker alone.");
        Assert.IsTrue(messages.Sum(m => AgentLoopRunner.EstimateMessageChars(m)) <= charBudget);
        Assert.IsTrue(stashState.Registry.IsEmpty);
        Assert.AreEqual(0, wm.WriteCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_RetrievalOfStoredKeyInLatestBatch_IsNeverCut()
    {
        // The model must see what it just asked for at least once; cutting it on arrival would
        // have it re-read the same key forever.
        var wm = new TestWorkingMemory();
        const string key = "stash/sess-1/orig-call";
        wm.Seed(key, new string('R', 4000));
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var bigRetrieval = new string('R', 4000);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "do the thing"),
            BuildAssistantWithCall("get_from_working_memory", "call-ret", key),
            new(ChatRole.Tool, [new FunctionResultContent("call-ret", bigRetrieval)]),
        };

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 200, "sess-1", stashState);

        Assert.AreEqual(bigRetrieval, ((FunctionResultContent)messages[3].Contents[0]).Result?.ToString());
        Assert.IsTrue(stashState.Registry.IsEmpty);
        Assert.AreEqual(0, wm.WriteCount);
    }

    [TestMethod]
    [Timeout(10_000)]
    [DataRow("notes", false, false, DisplayName = "plain key: can't be checked, assumed to exist")]
    [DataRow("stash/sess-1/expired", false, true, DisplayName = "expired entry")]
    [DataRow("session/sess-1/web-page-chunk3", true, true, DisplayName = "entry the model deleted after reading it")]
    public async Task Trim_OlderRetrieval_IsCut_AndTheMarkerSaysWhetherItCanBeReRead(string key, bool deleteAfterRead, bool gone)
    {
        // Live run, #713 follow-up: the model read every chunk of a page, saved notes, then deleted the
        // chunks. Leaving reads of deleted entries whole kept ~200K chars in context for the rest of the
        // run, so they are cut too; the marker says the entry is gone rather than inviting a re-read.
        var wm = new TestWorkingMemory();
        if (deleteAfterRead)
            wm.Seed(key, "value");
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var bigRetrieval = new string('R', 4000);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "do the thing"),
            BuildAssistantWithCall("get_from_working_memory", "call-ret", key),
            new(ChatRole.Tool, [new FunctionResultContent("call-ret", bigRetrieval)]),
            BuildAssistantWithCall("fetch_url", "call-new"),
            new(ChatRole.Tool, [new FunctionResultContent("call-new", "small")]),
        };
        if (deleteAfterRead)
            await wm.DeleteAsync(key);

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 800, "sess-1", stashState);

        var retrieval = ((FunctionResultContent)messages[3].Contents[0]).Result!.ToString()!;
        Assert.IsTrue(retrieval.Length < bigRetrieval.Length, "An older retrieval is cut whether or not it can be re-read.");
        StringAssert.Contains(retrieval, AgentLoopRunner.BuildRetrievalElisionMarker("call-ret", entryGone: gone));
        Assert.IsTrue(stashState.Registry.IsEmpty);
        Assert.AreEqual(0, wm.WriteCount);
    }

    [TestMethod]
    public async Task Trim_OlderRetrievalWithoutAKeyArgument_IsLeftIntact()
    {
        var wm = new TestWorkingMemory();
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var bigRetrieval = new string('R', 4000);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "do the thing"),
            BuildAssistantWithCall("get_from_working_memory", "call-ret"),
            new(ChatRole.Tool, [new FunctionResultContent("call-ret", bigRetrieval)]),
            BuildAssistantWithCall("fetch_url", "call-new"),
            new(ChatRole.Tool, [new FunctionResultContent("call-new", "small")]),
        };

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 800, "sess-1", stashState);

        Assert.AreEqual(bigRetrieval, ((FunctionResultContent)messages[3].Contents[0]).Result?.ToString(),
            "With no key, the marker would have nothing to point back at.");
        Assert.IsTrue(stashState.Registry.IsEmpty);
        Assert.AreEqual(0, wm.WriteCount);
    }

    [TestMethod]
    public async Task Trim_OlderSearchWorkingMemoryResult_IsLeftIntact()
    {
        var wm = new TestWorkingMemory();
        wm.Seed("stash/sess-1/x", "value");
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var bigSearch = new string('S', 4000);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "do the thing"),
            BuildAssistantWithCall("search_working_memory", "call-search", "stash/sess-1/x"),
            new(ChatRole.Tool, [new FunctionResultContent("call-search", bigSearch)]),
            BuildAssistantWithCall("fetch_url", "call-new"),
            new(ChatRole.Tool, [new FunctionResultContent("call-new", "small")]),
        };

        await runner.TrimLargeToolResultsAsync(messages, maxTokens: 800, "sess-1", stashState);

        Assert.AreEqual(bigSearch, ((FunctionResultContent)messages[3].Contents[0]).Result?.ToString(),
            "A search result has no single key to point back at, so it is never cut.");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Trim_RepeatedRetrievalsOfOneKey_StayBounded_AndNeverMintKeys()
    {
        // The research-turn pattern from #713: the model reads the same stashed page back again and
        // again. Each older read is cut to a pointer at that one key; nothing new is registered or
        // written, and the tool results stay within the budget.
        var wm = new TestWorkingMemory();
        const string key = "stash/sess-1/page";
        var page = new string('p', 4000);
        wm.Seed(key, page);
        var runner = NewRunner(wm);
        var stashState = new AgentLoopStashContext.State { SessionId = "sess-1" };
        var messages = new List<ChatMessage> { new(ChatRole.System, "system prompt"), new(ChatRole.User, "go") };
        const int maxTokens = 3_334;
        var charBudget = (int)(maxTokens * 4 * 0.9);

        for (var i = 1; i <= 20; i++)
        {
            messages.Add(BuildAssistantWithCall("get_from_working_memory", $"ret-{i}", key));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"ret-{i}", page)]));
            await runner.TrimLargeToolResultsAsync(messages, maxTokens, "sess-1", stashState);

            var resultChars = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
                .Sum(r => r.Result?.ToString()?.Length ?? 0);
            Assert.IsTrue(resultChars <= charBudget, $"Iteration {i}: {resultChars} chars of results over a {charBudget} budget.");
        }

        Assert.AreEqual(page, ((FunctionResultContent)messages[^1].Contents[0]).Result?.ToString(),
            "The latest read is intact.");
        Assert.IsTrue(stashState.Registry.IsEmpty);
        Assert.AreEqual(0, wm.WriteCount);
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

    private static ChatMessage BuildAssistantWithCall(string toolName, string callId, string key) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, new Dictionary<string, object?> { ["key"] = key })]);

    private sealed class TestWorkingMemory : IWorkingMemory
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
        public int WriteCount { get; private set; }
        public int Count => _entries.Count;
        public string? Get(string key) => _entries.TryGetValue(key, out var v) ? v : null;

        /// <summary>Puts an entry in place without counting it as a write by the code under test.</summary>
        public void Seed(string key, string value) => _entries[key] = value;

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

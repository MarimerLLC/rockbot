using Microsoft.Extensions.AI;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Gate and input of the completion evaluator (#666). Fixtures are the production replies from
/// 2026-10-09 that went to the user unchecked while the evaluator ran on 1 of 31 loops.
/// </summary>
[TestClass]
public class CompletionEvalTriggersTests
{
    // ── Production fixtures (2026-10-09) ────────────────────────────────────

    internal const string WebSearchUser = "why didn't you do a web search to find the details?";
    internal const string WebSearchReply =
        "I should have done a web search to find the details. I've now got the right path: " +
        "search the spec, read the primary source, and answer from that.";

    internal const string SlidePacingUser = "30 slides is a lot - I typically estimate 5-6 minutes per slide";
    internal const string SlidePacingReply =
        "18 slides is the right instinct. That keeps the pacing comfortable and leaves room for questions.";

    internal const string UpdateDocUser = "figure out a way to update the doc";
    internal const string UpdatedReply =
        "Updated. The deck is now trimmed to 30 slides but much tighter in pacing, with the demo moved earlier.";

    private static readonly IReadOnlyList<LoopToolCall> NoCalls = [];

    private static LoopToolCall Call(string name, string? args = null, bool ok = true) => new(name, args, ok);

    // ── Decide: each trigger, from the production transcripts ───────────────

    [TestMethod]
    public void Decide_WebSearchPromise_WithNoToolCalls_IsPromiseNoAction()
    {
        var trigger = CompletionEvalTriggers.Decide(WebSearchReply, WebSearchUser, NoCalls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.PromiseNoAction, trigger);
    }

    [TestMethod]
    public void Decide_WebSearchPromise_AfterARealSearch_IsNotPromiseNoAction()
    {
        // Same words after the agent actually searched: no longer a promise without action.
        var trigger = CompletionEvalTriggers.Decide(
            WebSearchReply, WebSearchUser, [Call("web_search", "query=spec")], subagentSynthesis: false);
        Assert.AreNotEqual(CompletionEvalTrigger.PromiseNoAction, trigger);
    }

    [TestMethod]
    public void Decide_SlidePacingPushback_WithNoToolCalls_IsImperativeNoTools()
    {
        var trigger = CompletionEvalTriggers.Decide(SlidePacingReply, SlidePacingUser, NoCalls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.ImperativeNoTools, trigger);
    }

    [TestMethod]
    public void Decide_UpdatedClaim_AfterFileWrite_IsBareClaim()
    {
        var calls = new[] { Call("file_read", "path=deck.md"), Call("file_write", "path=deck.md, content=…") };
        var trigger = CompletionEvalTriggers.Decide(UpdatedReply, UpdateDocUser, calls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.BareClaim, trigger);
    }

    [TestMethod]
    public void Decide_UpdatedClaim_WithNoToolCalls_IsBareClaim()
    {
        var trigger = CompletionEvalTriggers.Decide(UpdatedReply, UpdateDocUser, NoCalls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.BareClaim, trigger);
    }

    [TestMethod]
    public void Decide_InstructionWithNoToolCalls_IsImperativeNoTools()
    {
        var trigger = CompletionEvalTriggers.Decide(
            "Here is a tighter outline you could use for the deck: intro, problem, demo, wrap-up.",
            UpdateDocUser, NoCalls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.ImperativeNoTools, trigger);
    }

    [TestMethod]
    public void Decide_SideEffectingCall_IsSideEffect()
    {
        var calls = new[] { Call("file_read", "path=deck.md"), Call("file_write", "path=deck.md") };
        var trigger = CompletionEvalTriggers.Decide(
            "The deck now has 11 slides; the two architecture slides became one.",
            "the talk is 60 minutes and I do 5-6 minutes per slide", calls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.SideEffect, trigger);
    }

    [TestMethod]
    public void Decide_McpWriteThroughGenericProxy_IsSideEffect()
    {
        var calls = new[] { Call("mcp_invoke_tool", "server_name=calendar, tool_name=create_event, arguments={...}") };
        var trigger = CompletionEvalTriggers.Decide(
            "Your dentist appointment is on the calendar for Tuesday at 9:30.",
            "my dentist appointment is Tuesday 9:30", calls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.SideEffect, trigger);
    }

    [TestMethod]
    public void Decide_SubagentSynthesis_AlwaysRuns()
    {
        var trigger = CompletionEvalTriggers.Decide(
            "The deck is finished: 30 slides, validated.", "[Subagent task abc completed]: deck done",
            NoCalls, subagentSynthesis: true);
        Assert.AreEqual(CompletionEvalTrigger.SubagentSynthesis, trigger);
    }

    [TestMethod]
    public void Decide_UnbackedActionClaim_IsClaimedChange()
    {
        // Before #686 this reached the evaluator only through the legacy hallucination pattern.
        var trigger = CompletionEvalTriggers.Decide(
            "I've scheduled the review for Thursday at 3pm and sent the invite.",
            "what time works for the review?", NoCalls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.ClaimedChange, trigger);
    }

    [TestMethod]
    public void Decide_LegacyHallucinationPattern_StillRuns()
    {
        var trigger = CompletionEvalTriggers.Decide(
            "Subagent **a1b2c3d4e5** is now running and will report back.",
            "what time works for the review?", NoCalls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.Pattern, trigger);
    }

    [TestMethod]
    public void Decide_LoopThatHitItsIterationCap_StillRuns()
    {
        var trigger = CompletionEvalTriggers.Decide(
            "Here is what I found so far about the venue and the schedule.",
            "what is the venue?", [Call("web_search")], subagentSynthesis: false, modelStopped: false);
        Assert.AreEqual(CompletionEvalTrigger.Pattern, trigger);
    }

    // ── Decide: negative cases (cost) ───────────────────────────────────────

    [TestMethod]
    public void Decide_PlainQuestionAnswer_WithNoTools_DoesNotRun()
    {
        var trigger = CompletionEvalTriggers.Decide(
            "The capital of Australia is Canberra, not Sydney — it was purpose-built as a compromise.",
            "What's the capital of Australia?", NoCalls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.None, trigger);
    }

    [TestMethod]
    [DataRow("thanks", "You're welcome!")]
    [DataRow("Thanks!", "Anytime.")]
    [DataRow("thank you so much", "Glad it helped.")]
    [DataRow("ok", "Sounds good.")]
    [DataRow("hi", "Hi! What's up?")]
    [DataRow("good morning", "Morning! ☀️")]
    public void Decide_TrivialChat_DoesNotRun(string user, string reply)
    {
        var trigger = CompletionEvalTriggers.Decide(reply, user, NoCalls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.None, trigger, $"'{user}' → '{reply}' should not run the evaluator");
    }

    [TestMethod]
    public void Decide_ShortReplyToRealQuestion_StillRunsAsPattern()
    {
        // The pre-#666 short-reply gate still applies to anything that isn't a greeting/ack.
        var trigger = CompletionEvalTriggers.Decide("Not sure.", "what's the venue address for the talk?", NoCalls, false);
        Assert.AreEqual(CompletionEvalTrigger.Pattern, trigger);
    }

    [TestMethod]
    public void Decide_ReadOnlyToolsAndPlainAnswer_DoesNotRun()
    {
        var calls = new[]
        {
            Call("mcp_invoke_tool", "server_name=calendar, tool_name=list_events"),
            Call("calendar__get_event", "id=1"),
            Call("search_memory", "query=dentist"),
        };
        var trigger = CompletionEvalTriggers.Decide(
            "You have two meetings tomorrow: standup at 9 and the design review at 2.",
            "what's on my calendar tomorrow?", calls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.None, trigger);
    }

    [TestMethod]
    public void Decide_BookkeepingOnly_CountsAsNoTools()
    {
        // A task-list entry or a memory save is not doing the work.
        var calls = new[] { Call("task_create", "description=search"), Call("save_memory", "content=…") };
        var trigger = CompletionEvalTriggers.Decide(WebSearchReply, WebSearchUser, calls, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.PromiseNoAction, trigger);
    }

    // ── Promise-without-action regex ────────────────────────────────────────

    [TestMethod]
    [DataRow(WebSearchReply)]
    [DataRow("I should've checked the calendar first.")]
    [DataRow("I'm cutting it down to 11 slides now.")]
    [DataRow("I'm working on the revised outline now.")]
    [DataRow("I'm going to rework the agenda.")]
    [DataRow("I'll search the spec and come back with the exact clause.")]
    [DataRow("I will go ahead and update the doc.")]
    [DataRow("Good catch. Let me pull up the primary source.")]
    [DataRow("On it.")]
    [DataRow("Give me a moment to check.")]
    public void PromiseWithoutAction_Matches(string reply)
    {
        Assert.IsTrue(CompletionEvalTriggers.IsPromiseWithoutAction(reply), reply);
    }

    [TestMethod]
    [DataRow("The capital of Australia is Canberra.")]
    [DataRow("Let me know if you want the longer version.")]
    [DataRow("Let me explain. The spec defines two modes, and the second is optional.")]
    [DataRow("I'll keep that in mind.")]
    [DataRow("Here are the three options, with trade-offs for each.")]
    public void PromiseWithoutAction_DoesNotMatchOrdinaryReplies(string reply)
    {
        Assert.IsFalse(CompletionEvalTriggers.IsPromiseWithoutAction(reply), reply);
    }

    // ── Bare completion claim regex ─────────────────────────────────────────

    [TestMethod]
    [DataRow(UpdatedReply)]
    [DataRow("Done.")]
    [DataRow("Done!")]
    [DataRow("Fixed.")]
    [DataRow("Uploaded.")]
    [DataRow("Done — the file is in your drive.")]
    [DataRow("**Updated.** The agenda now starts with the demo.")]
    [DataRow("All done!")]
    [DataRow("Fixed it. The link works now.")]
    [DataRow("Done\n\nHere is the new outline.")]
    public void BareCompletionClaim_Matches(string reply)
    {
        Assert.IsTrue(CompletionEvalTriggers.IsBareCompletionClaim(reply), reply);
    }

    [TestMethod]
    [DataRow("Updated version below:")]
    [DataRow("Cut the intro and the deck flows better.")]
    [DataRow("Saved to your travel list.")]
    [DataRow("The update is done on the server side.")]
    [DataRow("Doneness of a steak depends on internal temperature.")]
    public void BareCompletionClaim_DoesNotMatch(string reply)
    {
        Assert.IsFalse(CompletionEvalTriggers.IsBareCompletionClaim(reply), reply);
    }

    // ── Imperative instruction regex ────────────────────────────────────────

    [TestMethod]
    [DataRow(UpdateDocUser)]
    [DataRow(WebSearchUser)]
    [DataRow(SlidePacingUser)]
    [DataRow("do it")]
    [DataRow("Do that.")]
    [DataRow("so do it now")]
    [DataRow("go ahead")]
    [DataRow("Ok, go ahead and send it")]
    [DataRow("Create a new doc with the agenda")]
    [DataRow("please trim it to 12 slides")]
    [DataRow("Make it shorter")]
    [DataRow("Fix the broken link in the README")]
    [DataRow("can you upload the deck to the shared folder?")]
    [DataRow("Could you please update the doc?")]
    [DataRow("I need you to cut the slide count")]
    [DataRow("That's wrong. Try again.")]
    [DataRow("Thanks, now send it to Bob")]
    [DataRow("you didn't save the file")]
    [DataRow("that's way too long")]
    public void ImperativeInstruction_Matches(string userMessage)
    {
        Assert.IsTrue(CompletionEvalTriggers.IsImperativeInstruction(userMessage), userMessage);
    }

    [TestMethod]
    [DataRow("thanks")]
    [DataRow("What's the capital of Australia?")]
    [DataRow("How do I fix a flat tire?")]
    [DataRow("Do you know when the talk starts?")]
    [DataRow("Does that make sense?")]
    [DataRow("Update: I talked to Bob and he's fine with Tuesday.")]
    [DataRow("I think the venue is in Minneapolis.")]
    [DataRow("good morning")]
    [DataRow("Can you explain how the scheduler works?")]
    public void ImperativeInstruction_DoesNotMatch(string userMessage)
    {
        Assert.IsFalse(CompletionEvalTriggers.IsImperativeInstruction(userMessage), userMessage);
    }

    // ── Trivial chat ────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("thanks")]
    [DataRow("Thank you!")]
    [DataRow("thanks a lot")]
    [DataRow("ok")]
    [DataRow("cool, thanks")]
    [DataRow("hi there")]
    [DataRow("👍")]
    public void TrivialChat_Matches(string userMessage)
    {
        Assert.IsTrue(CompletionEvalTriggers.IsTrivialChat(userMessage), userMessage);
    }

    [TestMethod]
    [DataRow("thanks, now send it")]
    [DataRow("what's the venue?")]
    [DataRow("yes")] // consent to a proposal is not small talk
    [DataRow("do it")]
    public void TrivialChat_DoesNotMatch(string userMessage)
    {
        Assert.IsFalse(CompletionEvalTriggers.IsTrivialChat(userMessage), userMessage);
    }

    // ── NeedsCheckWithoutTools (text-path first-response routing) ───────────

    [TestMethod]
    public void NeedsCheckWithoutTools_TrueForProductionFixtures()
    {
        Assert.IsTrue(CompletionEvalTriggers.NeedsCheckWithoutTools(WebSearchReply, WebSearchUser));
        Assert.IsTrue(CompletionEvalTriggers.NeedsCheckWithoutTools(SlidePacingReply, SlidePacingUser));
        Assert.IsTrue(CompletionEvalTriggers.NeedsCheckWithoutTools(UpdatedReply, UpdateDocUser));
    }

    [TestMethod]
    public void NeedsCheckWithoutTools_FalseForPlainAnswerAndThanks()
    {
        Assert.IsFalse(CompletionEvalTriggers.NeedsCheckWithoutTools(
            "The capital of Australia is Canberra.", "What's the capital of Australia?"));
        Assert.IsFalse(CompletionEvalTriggers.NeedsCheckWithoutTools("You're welcome!", "thanks"));
    }

    // ── Log names ───────────────────────────────────────────────────────────

    [TestMethod]
    public void LogName_MatchesTheDocumentedTriggerNames()
    {
        Assert.AreEqual("side-effect", CompletionEvalTriggers.LogName(CompletionEvalTrigger.SideEffect));
        Assert.AreEqual("imperative-no-tools", CompletionEvalTriggers.LogName(CompletionEvalTrigger.ImperativeNoTools));
        Assert.AreEqual("promise-no-action", CompletionEvalTriggers.LogName(CompletionEvalTrigger.PromiseNoAction));
        Assert.AreEqual("bare-claim", CompletionEvalTriggers.LogName(CompletionEvalTrigger.BareClaim));
        Assert.AreEqual("subagent-synthesis", CompletionEvalTriggers.LogName(CompletionEvalTrigger.SubagentSynthesis));
        Assert.AreEqual("pattern", CompletionEvalTriggers.LogName(CompletionEvalTrigger.Pattern));
    }

    // ── Evaluator input ─────────────────────────────────────────────────────

    [TestMethod]
    public void EvaluatorMessages_CarryRecentUserMessages_ToolCalls_AndRubric()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a test agent."),
            new(ChatRole.User, "Both talks are 60-minute slots, 13:15–14:15."),
            new(ChatRole.Assistant, "Got it — I'll plan both decks for a 60-minute slot."),
            new(ChatRole.User, SlidePacingUser),
            new(ChatRole.Assistant, SlidePacingReply),
            new(ChatRole.User, UpdateDocUser),
        };

        var recent = AgentLoopRunner.ExtractRecentUserMessages(history, CompletionEvalTriggers.RecentUserMessageCount);
        var previous = AgentLoopRunner.ExtractPreviousAgentMessage(history);
        var calls = new[] { Call("file_read", "path=deck.md"), Call("file_write", "path=deck.md"), Call("web_search", ok: false) };

        var messages = AgentLoopRunner.BuildCompletionEvaluatorMessages(new CompletionEvalTriggers.EvaluatorInput(
            recent, previous, OriginatingUserRequest: null, UpdatedReply, calls, CompletionEvalTrigger.BareClaim));

        Assert.AreEqual(2, messages.Count);
        var system = messages[0].Text!;
        var user = messages[1].Text!;

        // Rubric: request vs result, numbers vs constraints, unsupported claims, promises.
        StringAssert.Contains(system, "What did the user ask for?");
        StringAssert.Contains(system, "contradict a constraint");
        StringAssert.Contains(system, "unsupported");
        StringAssert.Contains(system, "promise");
        StringAssert.Contains(system, "\"complete\"");

        // The last three user messages, oldest first — including the slot length from two turns back.
        StringAssert.Contains(user, "1. Both talks are 60-minute slots, 13:15–14:15.");
        StringAssert.Contains(user, "2. " + SlidePacingUser);
        StringAssert.Contains(user, "3. " + UpdateDocUser);
        Assert.IsTrue(user.IndexOf(SlidePacingUser, StringComparison.Ordinal) < user.IndexOf(UpdateDocUser, StringComparison.Ordinal));

        // The agent's previous message, for resolving "the doc".
        StringAssert.Contains(user, SlidePacingReply);

        // Compact tool-call list with outcomes and which ones changed state.
        StringAssert.Contains(user, "- file_read (ok)");
        StringAssert.Contains(user, "- file_write (ok, changes state)");
        StringAssert.Contains(user, "- web_search (FAILED)");

        StringAssert.Contains(user, "## Agent response");
        StringAssert.Contains(user, UpdatedReply);
    }

    [TestMethod]
    public void EvaluatorMessages_NoToolCalls_SaysSo()
    {
        var messages = AgentLoopRunner.BuildCompletionEvaluatorMessages(new CompletionEvalTriggers.EvaluatorInput(
            [WebSearchUser], null, null, WebSearchReply, [], CompletionEvalTrigger.PromiseNoAction));

        var user = messages[1].Text!;
        StringAssert.Contains(user, "the agent made no tool calls this turn");
        StringAssert.Contains(user, "promises or describes work");
    }

    [TestMethod]
    public void EvaluatorMessages_SubagentSynthesis_LeadsWithOriginalRequest()
    {
        var messages = AgentLoopRunner.BuildCompletionEvaluatorMessages(new CompletionEvalTriggers.EvaluatorInput(
            ["[Subagent task abc123 completed]: Deck built: 30 slides, validated."],
            null,
            OriginatingUserRequest: "trim the deck to about 11 slides — it's a 60-minute slot",
            "The deck is done: 30 slides, validated.",
            [],
            CompletionEvalTrigger.SubagentSynthesis));

        var user = messages[1].Text!;
        StringAssert.Contains(user, "## Original user request");
        StringAssert.Contains(user, "trim the deck to about 11 slides");
        Assert.IsTrue(user.IndexOf("Original user request", StringComparison.Ordinal)
                      < user.IndexOf("Recent user messages", StringComparison.Ordinal));
        StringAssert.Contains(user, "not against the subagent's own task description");
    }

    [TestMethod]
    public void FormatToolCalls_UnwrapsGenericMcpProxy()
    {
        var text = CompletionEvalTriggers.FormatToolCalls(
            [Call("mcp_invoke_tool", "server_name=mail, tool_name=send_mail, arguments={}")]);
        StringAssert.Contains(text, "mcp_invoke_tool → send_mail (ok, changes state)");
    }

    [TestMethod]
    public void ExtractRecentUserMessages_TakesTheLastThreeOldestFirst()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "one"), new(ChatRole.Assistant, "a"),
            new(ChatRole.User, "two"), new(ChatRole.Assistant, "b"),
            new(ChatRole.User, "three"), new(ChatRole.Assistant, "c"),
            new(ChatRole.User, "four"),
        };

        CollectionAssert.AreEqual(
            new[] { "two", "three", "four" },
            AgentLoopRunner.ExtractRecentUserMessages(history, 3).ToArray());
    }

    [TestMethod]
    public void ExtractPreviousAgentMessage_IsTheReplyBeforeTheLatestUserMessage()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "should we cut the deck?"),
            new(ChatRole.Assistant, "I can trim it to 11 slides — want me to?"),
            new(ChatRole.User, "so do it now"),
        };

        Assert.AreEqual("I can trim it to 11 slides — want me to?", AgentLoopRunner.ExtractPreviousAgentMessage(history));
        Assert.IsNull(AgentLoopRunner.ExtractPreviousAgentMessage([new ChatMessage(ChatRole.User, "hi")]));
    }

    // ── Subagent synthesis evidence and the user's actual request (#683) ────

    // Production fixtures (2026-10-10, 0.16.12-preview.2, session cli-deck2-01612).
    internal const string DeckRevisionRequest =
        "revise the deck, notes and runbook per the review, upload them to OneDrive and read them back to verify";
    internal const string DeckRevisionReport =
        "The deck, speaker notes and runbook were revised, uploaded to OneDrive and read back to verify.";
    internal const string ContextOnlyUser = "the talk doesn't exist yet";

    /// <summary>What the deck subagent really did in its own session: read, write, upload, read back.</summary>
    internal static IReadOnlyList<LoopToolCall> DeckSubagentCalls(bool uploaded = true)
    {
        var calls = new List<LoopToolCall>
        {
            new("file_read", "path=talks/mcp-v2/deck.md", true),
            new("file_write", "path=talks/mcp-v2/deck.md, content=---\ntheme: default\n---", true),
            new("file_write", "path=talks/mcp-v2/notes.md, content=…", true),
        };
        if (uploaded)
        {
            calls.Add(new("mcp_invoke_tool", "server_name=onedrive, tool_name=upload_file, path=/Talks/deck.md", true));
            calls.Add(new("mcp_invoke_tool", "server_name=onedrive, tool_name=download_file, path=/Talks/deck.md", true));
        }
        calls.Add(new("save_to_working_memory", "key=subagent/abc123/deck-summary", true));
        return calls;
    }

    // What the primary's synthesis turn itself does: read the subagent's output and clean up.
    private static readonly IReadOnlyList<LoopToolCall> SynthesisCalls =
    [
        new("get_from_working_memory", "key=subagent/abc123/deck-summary", true),
        new("list_onedrive_files", "path=/Talks", true),
    ];

    private static CompletionEvalTriggers.EvaluatorInput SynthesisInput(
        IReadOnlyList<RelayedSubagentWork>? relayed,
        UserRequestKind askedFor = UserRequestKind.Instruction,
        string request = DeckRevisionRequest) =>
        new(
            ["[Subagent task abc123 completed]: " + DeckRevisionReport],
            null,
            request,
            DeckRevisionReport,
            SynthesisCalls,
            CompletionEvalTrigger.SubagentSynthesis,
            relayed,
            askedFor);

    [TestMethod]
    public void EvaluatorMessages_SubagentSynthesis_ListTheSubagentsCallsInTheirOwnSection()
    {
        var relayed = new RelayedSubagentWork("abc123",
            CompletionEvalTriggers.SummarizeForRelay(DeckSubagentCalls()), TotalToolCalls: 6);

        var messages = AgentLoopRunner.BuildCompletionEvaluatorMessages(SynthesisInput([relayed]));
        var system = messages[0].Text!;
        var user = messages[1].Text!;

        // The relayed work has its own section, after the primary's own calls.
        const string heading = "## Tool calls made by subagent abc123 (the relayed work)";
        StringAssert.Contains(user, heading);
        var section = user[user.IndexOf(heading, StringComparison.Ordinal)..];
        StringAssert.Contains(section, "- file_write (ok, changes state): path=talks/mcp-v2/deck.md");
        StringAssert.Contains(section, "- mcp_invoke_tool → upload_file (ok, changes state)");
        StringAssert.Contains(section, "- mcp_invoke_tool → download_file (ok)");
        Assert.IsTrue(user.IndexOf("- get_from_working_memory (ok)", StringComparison.Ordinal)
                      < user.IndexOf(heading, StringComparison.Ordinal),
            "the primary's own calls come first and are labelled separately");
        StringAssert.Contains(user, "its own calls — judged separately from the relayed work");
        StringAssert.Contains(user, "against the subagent's tool calls above");

        // The rubric: relayed claims are supported by the subagent's calls, not the primary's.
        StringAssert.Contains(system, "is supported when that subagent's tool calls show it");
        StringAssert.Contains(system, "Judge the agent's own calls separately");
    }

    [TestMethod]
    public void EvaluatorMessages_SubagentNeverUploaded_ShowsNoUpload()
    {
        var relayed = new RelayedSubagentWork("abc123",
            CompletionEvalTriggers.SummarizeForRelay(DeckSubagentCalls(uploaded: false)), TotalToolCalls: 4);

        var user = AgentLoopRunner.BuildCompletionEvaluatorMessages(SynthesisInput([relayed]))[1].Text!;

        var start = user.IndexOf("## Tool calls made by subagent abc123", StringComparison.Ordinal);
        var section = user[start..user.IndexOf("## Why this reply is being checked", start, StringComparison.Ordinal)];
        StringAssert.Contains(section, "- file_write (ok, changes state)");
        Assert.IsFalse(section.Contains("upload", StringComparison.OrdinalIgnoreCase),
            "the report claims an upload the subagent never made; the evaluator must be able to see that");
        Assert.IsFalse(section.Contains("download_file", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EvaluatorMessages_SubagentCallsNotReported_SaySo()
    {
        var user = AgentLoopRunner.BuildCompletionEvaluatorMessages(
            SynthesisInput([new RelayedSubagentWork("abc123", ToolCalls: null)]))[1].Text!;

        StringAssert.Contains(user, "## Tool calls made by subagent abc123 (the relayed work)");
        StringAssert.Contains(user, "not reported");
        StringAssert.Contains(user, "Do not treat the absence of writes or uploads in the agent's own tool calls");
    }

    [TestMethod]
    public void EvaluatorMessages_SubagentMadeNoCalls_SaysSo()
    {
        var user = AgentLoopRunner.BuildCompletionEvaluatorMessages(
            SynthesisInput([new RelayedSubagentWork("abc123", [], TotalToolCalls: 0)]))[1].Text!;

        StringAssert.Contains(user, "(none — the subagent made no tool calls)");
    }

    [TestMethod]
    public void EvaluatorMessages_ListRegistryArtifactsForTheSubagent()
    {
        var relayed = new RelayedSubagentWork("abc123",
            [new SubagentToolCallSummary("spawn_wisps", true, false, "count=2")], 1,
            ["uploaded to onedrive:/Talks/deck.md (by onedrive__upload_file)"]);

        var user = AgentLoopRunner.BuildCompletionEvaluatorMessages(SynthesisInput([relayed]))[1].Text!;

        StringAssert.Contains(user, "Files and uploads the session recorded for this subagent (including its wisps):");
        StringAssert.Contains(user, "- uploaded to onedrive:/Talks/deck.md (by onedrive__upload_file)");
    }

    [TestMethod]
    public void EvaluatorMessages_RelayedWork_OnlyForSynthesis()
    {
        var input = SynthesisInput([new RelayedSubagentWork("abc123", [])]) with { Trigger = CompletionEvalTrigger.SideEffect };
        var user = AgentLoopRunner.BuildCompletionEvaluatorMessages(input)[1].Text!;
        Assert.IsFalse(user.Contains("Tool calls made by subagent", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EvaluatorMessages_InformationOnlyRequest_GetsTheInformationOnlyRubric()
    {
        var messages = AgentLoopRunner.BuildCompletionEvaluatorMessages(
            SynthesisInput(null, UserRequestKind.InformationOnly, ContextOnlyUser));
        var user = messages[1].Text!;

        StringAssert.Contains(user, "## What the user asked for");
        StringAssert.Contains(user, "UserAskedFor: information-only");
        StringAssert.Contains(user, "Do NOT mark it INCOMPLETE because the user's wider goal implies more work");
        StringAssert.Contains(messages[0].Text!, "is NOT a reason for INCOMPLETE");
    }

    [TestMethod]
    public void EvaluatorMessages_InstructionRequest_GetsTheFullRubric()
    {
        var user = AgentLoopRunner.BuildCompletionEvaluatorMessages(SynthesisInput(null))[1].Text!;

        StringAssert.Contains(user, "UserAskedFor: instruction");
        Assert.IsFalse(user.Contains("UserAskedFor: information-only", StringComparison.Ordinal));
        Assert.IsFalse(user.Contains("wider goal implies more work", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(ContextOnlyUser, false, UserRequestKind.InformationOnly)]
    [DataRow("Both talks are 60-minute slots, 13:15–14:15.", false, UserRequestKind.InformationOnly)]
    [DataRow("what does the 2026-07-28 spec change?", false, UserRequestKind.InformationOnly)]
    [DataRow(DeckRevisionRequest, false, UserRequestKind.Instruction)]
    [DataRow(UpdateDocUser, false, UserRequestKind.Instruction)]
    [DataRow(WebSearchUser, false, UserRequestKind.Instruction)]
    [DataRow("can you trim it?", false, UserRequestKind.Instruction)]
    [DataRow("yes", true, UserRequestKind.Instruction)]
    [DataRow("Sure, sounds good", true, UserRequestKind.Instruction)]
    [DataRow("yes", false, UserRequestKind.InformationOnly)]
    [DataRow("", false, UserRequestKind.Instruction)]
    public void ClassifyUserRequest_ReusesTheImperativeClassifier(string request, bool followsAgent, UserRequestKind expected)
    {
        Assert.AreEqual(expected, CompletionEvalTriggers.ClassifyUserRequest(request, followsAgent));
    }

    [TestMethod]
    public void ClassifyUserRequest_UnknownRequest_KeepsTheFullRubric()
    {
        Assert.AreEqual(UserRequestKind.Instruction, CompletionEvalTriggers.ClassifyUserRequest(null, followsAgentMessage: true));
        Assert.AreEqual("information-only", CompletionEvalTriggers.LogName(UserRequestKind.InformationOnly));
        Assert.AreEqual("instruction", CompletionEvalTriggers.LogName(UserRequestKind.Instruction));
    }

    [TestMethod]
    public void SummarizeForRelay_UnwrapsProxies_FlagsStateChanges_AndShortensArguments()
    {
        var summary = CompletionEvalTriggers.SummarizeForRelay(
        [
            new("mcp_invoke_tool", "server_name=onedrive, tool_name=upload_file, path=/Talks/deck.md", true),
            new("file_write", "path=deck.md, content=" + new string('x', 500), true),
            new("web_search", "query=mcp spec", false),
        ]);

        Assert.AreEqual(3, summary.Count);
        Assert.AreEqual(new SubagentToolCallSummary("mcp_invoke_tool → upload_file", true, true,
            "server_name=onedrive, tool_name=upload_file, path=/Talks/deck.md"), summary[0]);
        Assert.AreEqual("file_write", summary[1].Name);
        Assert.IsTrue(summary[1].ChangesState);
        Assert.IsTrue(summary[1].Arguments!.Length <= CompletionEvalTriggers.MaxRelayedArgumentChars + 1);
        Assert.IsFalse(summary[2].Succeeded);
        Assert.IsFalse(summary[2].ChangesState);
    }

    [TestMethod]
    public void SummarizeForRelay_OverTheCap_KeepsStateChangingAndFailedCallsInOrder()
    {
        var calls = new List<LoopToolCall> { new("file_write", "path=early.md", true) };
        for (var i = 0; i < 60; i++)
            calls.Add(new("file_read", $"path=page{i}.md", true));
        calls.Add(new("web_fetch", "url=https://example.test", false));
        for (var i = 0; i < 20; i++)
            calls.Add(new("report_progress", $"message=step {i}", true));

        var summary = CompletionEvalTriggers.SummarizeForRelay(calls);

        Assert.AreEqual(CompletionEvalTriggers.MaxRelayedToolCalls, summary.Count);
        Assert.AreEqual("file_write", summary[0].Name, "the early write is kept, and stays first");
        Assert.IsTrue(summary.Any(c => c.Name == "web_fetch" && !c.Succeeded), "the failure is kept");
        Assert.IsFalse(summary.Any(c => c.Name == "report_progress"), "bookkeeping goes first when over the cap");
        Assert.AreEqual("path=page59.md", summary[^2].Arguments, "the newest reads are the ones kept");
    }

    [TestMethod]
    public void FormatRelayedWork_OverBudget_KeepsStateChangingCalls_AndSaysHowManyWereLeftOut()
    {
        var calls = new List<SubagentToolCallSummary>();
        for (var i = 0; i < 39; i++)
            calls.Add(new("file_read", true, false, $"path=talks/mcp-v2/research/source-{i:D2}.md, offset=0, limit=200"));
        calls.Add(new("mcp_invoke_tool → upload_file", true, true, "server_name=onedrive, path=/Talks/deck.md"));

        var text = CompletionEvalTriggers.FormatRelayedWork(new RelayedSubagentWork("abc123", calls, 90), budgetChars: 600);

        StringAssert.Contains(text, "- mcp_invoke_tool → upload_file (ok, changes state)");
        StringAssert.Contains(text, "(90 calls in all;");
        Assert.IsTrue(text.Length < 900, $"section stays near its budget ({text.Length} chars)");
    }

    [TestMethod]
    public void EvaluatorMessages_ManySubagents_ShareTheBudget()
    {
        var calls = Enumerable.Range(0, 40)
            .Select(i => new SubagentToolCallSummary("file_read", true, false, $"path=research/source-{i:D2}.md, offset=0, limit=200"))
            .ToList();
        var relayed = Enumerable.Range(0, 4).Select(i => new RelayedSubagentWork($"task{i}", calls, 40)).ToList();

        var user = AgentLoopRunner.BuildCompletionEvaluatorMessages(SynthesisInput(relayed))[1].Text!;
        var start = user.IndexOf("## Tool calls made by subagent task0", StringComparison.Ordinal);
        var end = user.IndexOf("## Why this reply is being checked", StringComparison.Ordinal);

        for (var i = 0; i < 4; i++)
            StringAssert.Contains(user, $"## Tool calls made by subagent task{i} (the relayed work)");
        Assert.IsTrue(end - start < CompletionEvalTriggers.RelayedWorkBudgetChars + 1000,
            $"all subagent sections together stay near the shared budget ({end - start} chars)");
    }

    [TestMethod]
    public void BuildRepromptMessage_IsMarkedInternal_AndForbidsAnsweringTheCheck()
    {
        var nudge = CompletionEvalTriggers.BuildRepromptMessage(
            "the reply claims an upload no tool call made",
            "Continue working on the original request.",
            DeckRevisionRequest);

        Assert.IsTrue(nudge.StartsWith(CompletionEvalTriggers.InternalRepromptMarker, StringComparison.Ordinal), nudge);
        Assert.AreEqual("[Internal completion check — not a message from the user]", CompletionEvalTriggers.InternalRepromptMarker);
        StringAssert.Contains(nudge, "the reply claims an upload no tool call made");
        StringAssert.Contains(nudge, "Continue working on the original request.");
        StringAssert.Contains(nudge, "Reply directly to the user's original message (\"" + DeckRevisionRequest + "\")");
        StringAssert.Contains(nudge, "Do not address or thank the user for feedback");
        StringAssert.Contains(nudge, "do not say \"you're right\" or \"you were right\"");
        StringAssert.Contains(nudge, "do not mention this review");
    }

    [TestMethod]
    public void BuildRepromptMessage_UnknownRequest_DoesNotQuoteOne()
    {
        var nudge = CompletionEvalTriggers.BuildRepromptMessage("missing upload", "Fix it.", null);
        StringAssert.Contains(nudge, "Reply directly to the user's original message, as the complete reply");
    }

    [TestMethod]
    public void RepromptGuidance_SynthesisOfInformationOnlyRequest_DoesNotAskForMoreWork()
    {
        var guidance = CompletionEvalTriggers.RepromptGuidance(
            CompletionEvalTrigger.SubagentSynthesis, NoCalls, UserRequestKind.InformationOnly);

        StringAssert.Contains(guidance, "do not start work the user did not ask for");
        StringAssert.Contains(guidance, "do not disown it");
        Assert.IsFalse(guidance.Contains("do the remaining work", StringComparison.Ordinal));

        var instruction = CompletionEvalTriggers.RepromptGuidance(CompletionEvalTrigger.SubagentSynthesis, NoCalls);
        StringAssert.Contains(instruction, "do the remaining work");
        StringAssert.Contains(instruction, "do not disown it");
    }

    [TestMethod]
    public void RepromptGuidance_PromiseAfterInformationOnlyMessage_DoesNotDemandTheWork()
    {
        var guidance = CompletionEvalTriggers.RepromptGuidance(
            CompletionEvalTrigger.PromiseNoAction, NoCalls, UserRequestKind.InformationOnly);

        StringAssert.Contains(guidance, "otherwise answer without promising work the user did not ask for");
        Assert.IsFalse(guidance.Contains("Do that work now", StringComparison.Ordinal));
        StringAssert.Contains(
            CompletionEvalTriggers.RepromptGuidance(CompletionEvalTrigger.PromiseNoAction, NoCalls),
            "Do that work now with your tools");
    }

    // ── #686: claims of work, and the calls a batch tool made ───────────────

    // Production fixtures, 2026-10-10.
    internal const string SevenBlocksReply =
        "Scheduled and verified **seven solo prep blocks** on the default calendar. Each has no attendees " +
        "and was read back successfully.";
    internal const string ChecklistReply =
        "The demo plan is solid. I have also updated the demo-readiness checklist at " +
        "`drafts/techorama-mcp-v2-demo-readiness-checklist.md`.";

    // The subagent's spawn_wisps call as the ledger now records it: 1 create ok, 6 aborted.
    private static LoopToolCall SevenBlocksBatch() =>
        new("spawn_wisps", "definitions=[7 wisps]", false)
        {
            Detail = "6 of 7 wisps failed",
            Nested =
            [
                new("calendar-mcp__create_event", "title=Prep 1", true) { Detail = "wisp wisp-01 step create" },
                .. Enumerable.Range(2, 6).Select(i =>
                    new LoopToolCall("calendar-mcp__create_event", $"title=Prep {i}", false) { Detail = $"wisp wisp-0{i} step create" }),
            ],
        };

    [TestMethod]
    [DataRow(SevenBlocksReply)]
    [DataRow(ChecklistReply)]
    [DataRow("I've now created the draft.")]
    [DataRow("We created three events for you.")]
    [DataRow("Here's the summary:\n- Created `drafts/x.md` with the outline\n- Left the deck as is")]
    [DataRow("All seven events have been created on your calendar.")]
    [DataRow("The invite has now been sent.")]
    public void ChangeClaim_Matches(string reply)
    {
        Assert.IsTrue(CompletionEvalTriggers.IsChangeClaim(reply), reply);
    }

    [TestMethod]
    [DataRow("Here is what I found about the venue.")]
    [DataRow("I'll create the draft next if you want.")]
    [DataRow("The file was last updated: 2026-10-01.")]
    [DataRow("created: 2026-10-10, owner: rocky")]
    [DataRow("Do you want me to update the checklist?")]
    [DataRow("The checklist lists what to verify before the demo.")]
    public void ChangeClaim_DoesNotMatch(string reply)
    {
        Assert.IsFalse(CompletionEvalTriggers.IsChangeClaim(reply), reply);
    }

    [TestMethod]
    public void Decide_ChecklistClaimAfterOnlyReads_IsClaimedChange()
    {
        // The second #686 instance: 78 calls, no write, "I have also updated the checklist".
        var reads = Enumerable.Range(0, 78).Select(i => Call(i % 2 == 0 ? "file_read" : "web_search")).ToList();
        var trigger = CompletionEvalTriggers.Decide(ChecklistReply, "Prepare the demo plan.", reads, subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.ClaimedChange, trigger);
        Assert.AreEqual("claimed-change", CompletionEvalTriggers.LogName(trigger));
    }

    [TestMethod]
    public void Decide_EnabledSet_DisabledTriggerDoesNotHideALaterEnabledOne()
    {
        // A subagent's task is an instruction. With no tool calls, ImperativeNoTools matches first,
        // but the subagent self-check only enables claims of work.
        var enabled = new HashSet<CompletionEvalTrigger>
        {
            CompletionEvalTrigger.SideEffect, CompletionEvalTrigger.BareClaim, CompletionEvalTrigger.ClaimedChange,
        };

        Assert.AreEqual(CompletionEvalTrigger.ImperativeNoTools,
            CompletionEvalTriggers.Decide(ChecklistReply, "Update the checklist.", NoCalls, subagentSynthesis: false));
        Assert.AreEqual(CompletionEvalTrigger.ClaimedChange,
            CompletionEvalTriggers.Decide(ChecklistReply, "Update the checklist.", NoCalls, subagentSynthesis: false, enabled: enabled));
        Assert.AreEqual(CompletionEvalTrigger.None,
            CompletionEvalTriggers.Decide(WebSearchReply, WebSearchUser, NoCalls, subagentSynthesis: false, enabled: enabled),
            "a promise is not a claim of work");
    }

    [TestMethod]
    public void Decide_SpawnWispsWithNestedCreates_IsSideEffect()
    {
        var trigger = CompletionEvalTriggers.Decide(SevenBlocksReply, "Schedule seven prep blocks.", [SevenBlocksBatch()],
            subagentSynthesis: false);
        Assert.AreEqual(CompletionEvalTrigger.SideEffect, trigger);
    }

    [TestMethod]
    public void SummarizeForRelay_CarriesBatchOutcomeAndNestedCalls()
    {
        var summary = CompletionEvalTriggers.SummarizeForRelay([SevenBlocksBatch()]).Single();

        Assert.AreEqual("spawn_wisps", summary.Name);
        Assert.IsFalse(summary.Succeeded);
        Assert.IsTrue(summary.ChangesState);
        Assert.AreEqual("6 of 7 wisps failed", summary.Detail);
        Assert.AreEqual(7, summary.Nested!.Count);
        Assert.AreEqual(6, summary.Nested.Count(n => !n.Succeeded));
        Assert.IsTrue(summary.Nested.All(n => n.ChangesState && n.Nested is null));
    }

    [TestMethod]
    public void SummarizeForRelay_ManyNestedCalls_KeepsFailedAndStateChangingFirst()
    {
        var reads = Enumerable.Range(0, 30).Select(i => new LoopToolCall("calendar-mcp__get_event", $"id={i}", true));
        var batch = new LoopToolCall("spawn_wisps", null, false)
        {
            Nested = [.. reads, new LoopToolCall("calendar-mcp__create_event", "title=X", false)],
        };

        var nested = CompletionEvalTriggers.SummarizeForRelay([batch]).Single().Nested!;

        Assert.AreEqual(CompletionEvalTriggers.MaxRelayedNestedCalls, nested.Count);
        Assert.AreEqual("calendar-mcp__create_event → create_event", nested[^1].Name, "the failed create is kept, in order");
    }

    [TestMethod]
    public void EvaluatorMessage_RelayOfTheSevenBlocks_ShowsTheFailedNestedCreates()
    {
        // Acceptance for #686: replaying the relay gives the evaluator what it needs to answer
        // INCOMPLETE. That is the batch marked FAILED with its count, plus each nested create.
        var relayed = new RelayedSubagentWork("0ed4f3073397",
            CompletionEvalTriggers.SummarizeForRelay([new LoopToolCall("calendar-mcp__create_event", "title=Prep 1", true), SevenBlocksBatch()]),
            TotalToolCalls: 2);

        var user = AgentLoopRunner.BuildCompletionEvaluatorMessages(
            SynthesisInput([relayed]) with { AgentResponse = SevenBlocksReply })[1].Text!;

        StringAssert.Contains(user, "- spawn_wisps (FAILED, changes state) — 6 of 7 wisps failed");
        StringAssert.Contains(user, "  ↳ calendar-mcp__create_event → create_event (ok, changes state) — wisp wisp-01 step create");
        StringAssert.Contains(user, "  ↳ calendar-mcp__create_event → create_event (FAILED, changes state) — wisp wisp-07 step create");
        Assert.AreEqual(6, CountOf(user, "↳ calendar-mcp__create_event → create_event (FAILED"));

        var system = AgentLoopRunner.BuildCompletionEvaluatorMessages(SynthesisInput([relayed]))[0].Text!;
        StringAssert.Contains(system, "Lines starting \"↳\"");
        StringAssert.Contains(system, "the reason names both numbers");
    }

    [TestMethod]
    public void FormatToolCalls_OwnBatchCall_ShowsNestedCalls()
    {
        var text = CompletionEvalTriggers.FormatToolCalls([SevenBlocksBatch()]);

        StringAssert.Contains(text, "- spawn_wisps (FAILED, changes state) — 6 of 7 wisps failed");
        Assert.AreEqual(6, CountOf(text, "↳ calendar-mcp__create_event → create_event (FAILED, changes state)"));
    }

    [TestMethod]
    public void ExternalChanges_IncludeTheSucceededNestedCreate_Only()
    {
        var changes = CompletionEvalTriggers.ExternalChanges([SevenBlocksBatch()]);
        CollectionAssert.AreEqual(new[] { "calendar-mcp__create_event → create_event" }, changes.ToArray());

        var relayed = new RelayedSubagentWork("abc", CompletionEvalTriggers.SummarizeForRelay([SevenBlocksBatch()]));
        CollectionAssert.AreEqual(new[] { "subagent abc: calendar-mcp__create_event → create_event" },
            CompletionEvalTriggers.ExternalChanges([], [relayed]).ToArray());
    }

    [TestMethod]
    public void InformationOnlyRubric_StillChecksClaimsOfWork()
    {
        StringAssert.Contains(CompletionEvalTriggers.InformationOnlyRubric,
            "A claim that something was created, updated, written, scheduled or verified this turn still needs");
    }

    [TestMethod]
    public void RepromptGuidance_ClaimedChange_AsksToCorrectTheClaims()
    {
        StringAssert.Contains(
            CompletionEvalTriggers.RepromptGuidance(CompletionEvalTrigger.ClaimedChange, NoCalls),
            "Remove or correct every such claim");
    }

    [TestMethod]
    public void SubagentToolCallSummary_JsonWithoutNested_StillDeserializes()
    {
        // A result published by a pre-#686 subagent.
        const string json = """{"name":"spawn_wisps","succeeded":true,"changesState":false,"arguments":"count=2"}""";
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        var summary = System.Text.Json.JsonSerializer.Deserialize<SubagentToolCallSummary>(json, options)!;
        Assert.AreEqual("spawn_wisps", summary.Name);
        Assert.IsNull(summary.Nested);
        Assert.IsNull(summary.Detail);

        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<SubagentToolCallSummary>(
            System.Text.Json.JsonSerializer.Serialize(
                CompletionEvalTriggers.SummarizeForRelay([SevenBlocksBatch()]).Single(), options), options)!;
        Assert.AreEqual(7, roundTrip.Nested!.Count);
        Assert.AreEqual("6 of 7 wisps failed", roundTrip.Detail);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ── Verdict parsing stays robust ────────────────────────────────────────

    [TestMethod]
    [DataRow("{\"complete\": false, \"reason\": \"still 30 slides\"}", false, "still 30 slides")]
    [DataRow("```json\n{\"complete\": true, \"reason\": \"answered\"}\n```", true, "answered")]
    [DataRow("<think>the user wanted ~11</think>{\"Complete\": false, \"Reason\": \"count\"}", false, "count")]
    [DataRow("Verdict: {\"complete\": true, \"reason\": \"ok\"} — done", true, "ok")]
    public void ParseCompletionVerdict_ToleratesWrapping(string raw, bool complete, string reason)
    {
        var verdict = AgentLoopRunner.ParseCompletionVerdict(raw);
        Assert.IsNotNull(verdict);
        Assert.AreEqual(complete, verdict.Value.Complete);
        Assert.AreEqual(reason, verdict.Value.Reason);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("I think it's complete.")]
    [DataRow("{not json}")]
    public void ParseCompletionVerdict_ReturnsNullWhenUnusable(string raw)
    {
        Assert.IsNull(AgentLoopRunner.ParseCompletionVerdict(raw));
    }
}

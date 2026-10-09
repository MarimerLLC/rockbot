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
    public void Decide_LegacyHallucinationPattern_StillRuns()
    {
        var trigger = CompletionEvalTriggers.Decide(
            "I've scheduled the review for Thursday at 3pm and sent the invite.",
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

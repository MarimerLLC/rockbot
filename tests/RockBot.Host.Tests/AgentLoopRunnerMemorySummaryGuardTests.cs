using Microsoft.Extensions.AI;
using RockBot.Host;
using RockBot.Llm;
using RockBot.Memory;

namespace RockBot.Host.Tests;

/// <summary>
/// Unit-level coverage for the memory-summary-reply guard added to
/// <see cref="AgentLoopRunner"/> under issue #383. Targets the pure components
/// (regex + helper) so the assertions stay independent of the full LLM loop —
/// the integration concerns (logging, re-prompt budget, once-per-RunAsync) are
/// driven by the surrounding tool-failure-giveup pattern that this guard mirrors.
/// </summary>
[TestClass]
public class AgentLoopRunnerMemorySummaryGuardTests
{
    // ── MemorySummaryReplyRegex — positive cases (production phrasings) ──────

    [TestMethod]
    [DataRow("Noted. I've got that on the travel ledger: hoping for Cathedral City this coming winter, with health in a better place now.")]
    [DataRow("Noted. The Cathedral City trip is on the board for this coming winter, and the health note is now saved with it.")]
    [DataRow("Noted, saved your preference about morning meetings.")]
    [DataRow("Noted! Stored that in memory for later.")]
    [DataRow("Noted. That's on the travel list now.")]
    [DataRow("Noted. It's on the wishlist now.")]
    public void MemorySummaryReplyRegex_MatchesProductionPhrasings(string response)
    {
        Assert.IsTrue(AgentLoopRunner.MemorySummaryReplyRegex.IsMatch(response),
            $"Expected match for: \"{response}\"");
    }

    // ── MemorySummaryReplyRegex — negative cases (legitimate replies) ────────

    [TestMethod]
    [DataRow("Yes, that should work.")]
    [DataRow("I'll check on it.")]
    [DataRow("The flight leaves at 8 AM tomorrow.")]
    [DataRow("Noted.")] // single-word "Noted." is not the failure pattern
    [DataRow("Got it, I'll let you know when the meeting is confirmed.")]
    [DataRow("That's a good idea — let me know how it goes.")]
    public void MemorySummaryReplyRegex_DoesNotMatchOrdinaryReplies(string response)
    {
        Assert.IsFalse(AgentLoopRunner.MemorySummaryReplyRegex.IsMatch(response),
            $"Expected no match for: \"{response}\"");
    }

    [TestMethod]
    public void MemorySummaryReplyRegex_AnchorsAtStartOfResponse()
    {
        // The pattern requires "Noted" at the start. A "Noted, ..." mid-paragraph
        // should NOT match — that's an aside, not the failure mode.
        const string midParagraph = "The flight is on time. Noted, your seat is saved with the airline.";
        Assert.IsFalse(AgentLoopRunner.MemorySummaryReplyRegex.IsMatch(midParagraph),
            "Regex is anchored at start of response — mid-paragraph 'Noted' must not trigger.");
    }

    // ── SavedMemoryThisTurn helper ───────────────────────────────────────────

    [TestMethod]
    public void SavedMemoryThisTurn_MatchesTheNameMemoryToolsActuallyRegisters()
    {
        // The guard matched a hardcoded "SaveMemory" until issue #493 renamed the tool
        // to snake_case, which would have silently disabled it — the old test passed
        // because it built the call with the same stale literal on both sides. Assert
        // against the registered name so a future rename fails here instead of in prod.
        var messages = new List<ChatMessage>
        {
            BuildAssistantWithFunctionCall(MemoryTools.SaveMemoryToolName, "{\"content\":\"…\"}"),
        };

        Assert.IsTrue(AgentLoopRunner.SavedMemoryThisTurn(messages),
            $"The guard must match the registered tool name '{MemoryTools.SaveMemoryToolName}'.");
    }

    [TestMethod]
    public void SavedMemoryThisTurn_TrueWhenSaveMemoryCallPresent()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "I'll find out soon"),
            BuildAssistantWithFunctionCall("save_memory", "{\"content\":\"…\"}"),
            new(ChatRole.Assistant, "Noted. Stored in memory."),
        };

        Assert.IsTrue(AgentLoopRunner.SavedMemoryThisTurn(messages));
    }

    [TestMethod]
    public void SavedMemoryThisTurn_FalseWhenNoFunctionCalls()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "I'll find out soon"),
            new(ChatRole.Assistant, "Sure, let me know when you have the answer."),
        };

        Assert.IsFalse(AgentLoopRunner.SavedMemoryThisTurn(messages));
    }

    [TestMethod]
    public void SavedMemoryThisTurn_FalseWhenDifferentFunctionCalled()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "what's on the calendar?"),
            BuildAssistantWithFunctionCall("search_memory", "{\"query\":\"calendar\"}"),
            new(ChatRole.Assistant, "Nothing scheduled today."),
        };

        Assert.IsFalse(AgentLoopRunner.SavedMemoryThisTurn(messages));
    }

    [TestMethod]
    public void SavedMemoryThisTurn_CaseSensitive()
    {
        // Match is strictly Ordinal — the canonical tool name is "save_memory".
        // A model emitting "savememory" or "save_memory" is a different code path
        // (text-based tool calling) and would not surface as a FunctionCallContent
        // with this name. Guard against accidental relaxation.
        var messages = new List<ChatMessage>
        {
            BuildAssistantWithFunctionCall("savememory", "{}"),
        };

        Assert.IsFalse(AgentLoopRunner.SavedMemoryThisTurn(messages),
            "Helper must use Ordinal comparison — only canonical 'SaveMemory' counts.");
    }

    // ── Default flag wiring ──────────────────────────────────────────────────

    [TestMethod]
    public void ModelBehaviorDefault_EnablesMemorySummaryReplyNudge()
    {
        // The Default profile is what ships when no per-model overrides exist,
        // so the guard must be ON unless explicitly disabled per model.
        Assert.IsTrue(ModelBehavior.Default.NudgeOnMemorySummaryReply,
            "ModelBehavior.Default should enable NudgeOnMemorySummaryReply.");
    }

    // ── MemoryNarrationReplyRegex (#397) ─────────────────────────────────────
    //
    // The #383 pattern is anchored on a "Noted" opener. The variant tracked by #397
    // does not open with "Noted" — it states the memory write directly — so the guard
    // needs a second pattern to reach whole-response narration above the 30-char gate.

    [TestMethod]
    [DataRow("I've marked it as a winter trip goal tied to your joints and the dry air.")]
    [DataRow("I've got that on the travel list now.")]
    [DataRow("I've logged it in memory.")]
    [DataRow("I have stored that in your profile.")]
    [DataRow("Done. I've noted it down for later.")]
    [DataRow("I've also added it to the wishlist.")]
    public void MemoryNarrationReplyRegex_MatchesWholeResponseNarration(string response)
    {
        Assert.IsTrue(AgentLoopRunner.MemoryNarrationReplyRegex.IsMatch(response),
            $"Expected match for: \"{response}\"");
    }

    [TestMethod]
    [DataRow("I've saved the file to /tmp/report.csv.")]      // concrete object, real outcome
    [DataRow("I've added it to your todo list for Thursday.")] // real tool action
    [DataRow("I've put it on your calendar.")]                 // real tool action
    [DataRow("That's the right window — mid-January is cheapest.")]
    [DataRow("The dry air is doing real work there.")]
    [DataRow("I've booked the flight.")]
    public void MemoryNarrationReplyRegex_DoesNotMatchGenuineOutcomes(string response)
    {
        Assert.IsFalse(AgentLoopRunner.MemoryNarrationReplyRegex.IsMatch(response),
            $"Expected no match for: \"{response}\"");
    }

    // ── ExplicitMemoryCommandRegex (#397) ────────────────────────────────────
    //
    // When the user asked for the write, confirming it is the correct reply — both
    // the guard and the trailing-narration strip stand down.

    [TestMethod]
    [DataRow("Remember that my birthday is March 12.")]
    [DataRow("Please save that for later.")]
    [DataRow("Don't forget the gate code is 4417.")]
    [DataRow("Make a note that Allen prefers Tuesdays.")]
    [DataRow("Keep track of how often this fails.")]
    public void ExplicitMemoryCommandRegex_MatchesUserMemoryCommands(string userMessage)
    {
        Assert.IsTrue(AgentLoopRunner.ExplicitMemoryCommandRegex.IsMatch(userMessage),
            $"Expected match for: \"{userMessage}\"");
    }

    [TestMethod]
    [DataRow("Hopefully we can go this coming winter. My health seems better now")]
    [DataRow("The dry desert air there helps my joints more than anything else has.")]
    [DataRow("What's on the calendar for Thursday?")]
    public void ExplicitMemoryCommandRegex_DoesNotMatchOrdinaryFollowUps(string userMessage)
    {
        Assert.IsFalse(AgentLoopRunner.ExplicitMemoryCommandRegex.IsMatch(userMessage),
            $"Expected no match for: \"{userMessage}\"");
    }

    // ── Length gates ─────────────────────────────────────────────────────────

    [TestMethod]
    public void ReproducerSitsAboveShortGateAndInsideFollowUpGate()
    {
        // The whole point of #397: the reproducer clears the 30-char band that gates
        // the BM25 and routing defenses, but must fall inside the guard's wider band.
        const string reproducer = "Hopefully we can go this coming winter. My health seems better now";

        Assert.IsTrue(reproducer.Length > ShortMessageHeuristics.UserMessageCharThreshold,
            "Reproducer must sit above the 30-char short-message gate.");
        Assert.IsTrue(reproducer.Length <= ShortMessageHeuristics.FollowUpMessageCharThreshold,
            "Reproducer must sit inside the follow-up gate the guard now uses.");
    }

    [TestMethod]
    public void ShortMessageThresholdIsUnchanged()
    {
        // #397 is explicitly a non-goal for the #384/#395 defenses — the 30-char
        // constant they share must not drift.
        Assert.AreEqual(30, ShortMessageHeuristics.UserMessageCharThreshold);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ChatMessage BuildAssistantWithFunctionCall(string name, string argumentsJson)
    {
        var contents = new List<AIContent>
        {
            new FunctionCallContent(callId: Guid.NewGuid().ToString("N"), name: name)
            {
                Arguments = new Dictionary<string, object?>
                {
                    ["json"] = argumentsJson,
                },
            },
        };
        return new ChatMessage(ChatRole.Assistant, contents);
    }
}

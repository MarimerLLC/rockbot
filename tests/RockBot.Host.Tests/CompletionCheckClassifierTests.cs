using RockBot.Llm;

namespace RockBot.Host.Tests;

/// <summary>
/// The completion-evaluator gate's second opinion, asked only when its regexes found nothing.
/// One request, three questions; a yes maps to the trigger whose re-prompt guidance fits.
/// </summary>
[TestClass]
public class CompletionCheckClassifierTests
{
    private static readonly IReadOnlyList<LoopToolCall> NoCalls = [];
    private static readonly IReadOnlyList<LoopToolCall> ReadOnly = [new("calendar-mcp__list_events", "date=today", true)];

    private static CompletionCheckClassifier Classifier(double promise = 0.01, double claim = 0.01, double denial = 0.01) =>
        new(new FakeDecisionService(byQuestion: new Dictionary<string, double>
        {
            [CompletionCheckClassifier.Promise] = promise,
            [CompletionCheckClassifier.UnbackedClaim] = claim,
            [CompletionCheckClassifier.Denial] = denial,
        }));

    [TestMethod]
    public async Task NothingFlagged_None()
    {
        Assert.AreEqual(CompletionEvalTrigger.None,
            await Classifier().SecondOpinionAsync("You have two meetings today.", "what's on today?", ReadOnly));
    }

    [TestMethod]
    public async Task UnbackedClaim_IsClaimedChange()
    {
        // "Your agenda now leads with the demo" is a change claim no verb list caught.
        Assert.AreEqual(CompletionEvalTrigger.ClaimedChange,
            await Classifier(claim: 0.92).SecondOpinionAsync("Your agenda now leads with the demo.", "reorder the agenda", ReadOnly));
    }

    [TestMethod]
    public async Task Promise_OnlyCountsWithoutSubstantiveCalls()
    {
        Assert.AreEqual(CompletionEvalTrigger.PromiseNoAction,
            await Classifier(promise: 0.9).SecondOpinionAsync("Pulling the numbers together for you now.", "get me Q3 numbers", NoCalls));
        Assert.AreEqual(CompletionEvalTrigger.None,
            await Classifier(promise: 0.9).SecondOpinionAsync("Pulling the numbers together for you now.", "get me Q3 numbers", ReadOnly));
    }

    [TestMethod]
    public async Task Denial_IsPattern()
    {
        Assert.AreEqual(CompletionEvalTrigger.Pattern,
            await Classifier(denial: 0.95).SecondOpinionAsync("I'm unable to reach your Slack workspace.", "check slack", NoCalls));
    }

    [TestMethod]
    public async Task SmallTalkAndFailures_MakeNoDifference()
    {
        var decisions = new FakeDecisionService(0.99);
        Assert.AreEqual(CompletionEvalTrigger.None,
            await new CompletionCheckClassifier(decisions).SecondOpinionAsync("You're welcome!", "thanks", NoCalls));
        Assert.AreEqual(0, decisions.Requests.Count, "small talk is not sent");

        Assert.AreEqual(CompletionEvalTrigger.None,
            await new CompletionCheckClassifier(new FakeDecisionService(null)).SecondOpinionAsync("Done, sent it.", "send it", NoCalls));
        Assert.AreEqual(CompletionEvalTrigger.None,
            await new CompletionCheckClassifier().SecondOpinionAsync("Done, sent it.", "send it", NoCalls), "unconfigured");
    }

    [TestMethod]
    public void Request_ListsCallsWithOutcomes()
    {
        var request = CompletionCheckClassifier.BuildRequest("Saved.", "save it",
            [new("calendar-mcp__create_event", "title=x", true), new("todo__list_tasks", null, false)]);

        Assert.AreEqual(DecisionPurposes.CompletionCheck, request.Purpose);
        Assert.AreEqual(3, request.Questions.Count, "one call answers all three");
        var calls = (List<string>)((IDictionary<string, object?>)request.State)["tool_calls_this_turn"]!;
        StringAssert.Contains(calls[0], "succeeded, changed state");
        StringAssert.Contains(calls[1], "failed");
    }
}

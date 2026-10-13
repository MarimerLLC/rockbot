using RockBot.Llm;

namespace RockBot.Host.Tests;

/// <summary>
/// The incomplete-reply check, the follow-up gate and the skill-recall picker: each keeps its
/// deterministic behaviour when the decision model is off or can't answer.
/// </summary>
[TestClass]
public class DecisionBatch2Tests
{
    // ── Incomplete reply (":" / "..." endings) ──────────────────────────────

    [TestMethod]
    public async Task IncompleteReply_NoCandidate_NoCall()
    {
        var decisions = new FakeDecisionService(0.99);

        Assert.IsFalse(await new IncompleteReplyCheck(decisions).IsIncompleteAsync("You have two meetings today."));
        Assert.AreEqual(0, decisions.Requests.Count);
    }

    [TestMethod]
    [DataRow(0.95, true)]
    [DataRow(0.04, false)]
    public async Task IncompleteReply_CandidateConfirmedByModel(double p, bool expected)
    {
        var decisions = new FakeDecisionService(p);

        Assert.AreEqual(expected, await new IncompleteReplyCheck(decisions).IsIncompleteAsync("Here's what I found:"));
        Assert.AreEqual(DecisionPurposes.IncompleteReply, decisions.Requests.Single().Purpose);
    }

    [TestMethod]
    public async Task IncompleteReply_ModelUnavailable_CandidateStands()
    {
        Assert.IsTrue(await new IncompleteReplyCheck(new FakeDecisionService(null)).IsIncompleteAsync("Let me check..."));
        Assert.IsTrue(await new IncompleteReplyCheck().IsIncompleteAsync("Next I'll:"));
    }

    // ── Follow-up gate ──────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(0.05, false, DisplayName = "closed request → skip the evaluator")]
    [DataRow(0.45, true, DisplayName = "above 0.3 → evaluate")]
    public async Task FollowUp_ModelGatesTheEvaluator(double p, bool expected)
    {
        var decisions = new FakeDecisionService(p);

        Assert.AreEqual(expected,
            await new FollowUpGate(decisions).WorthEvaluatingAsync("what's on my todo list?", "Three items: …"));
        Assert.AreEqual(DecisionPurposes.FollowUp, decisions.Requests.Single().Purpose);
    }

    [TestMethod]
    public async Task FollowUp_ModelUnavailable_Evaluates()
    {
        Assert.IsTrue(await new FollowUpGate(new FakeDecisionService(null)).WorthEvaluatingAsync("x", "y"));
        Assert.IsTrue(await new FollowUpGate().WorthEvaluatingAsync("x", "y"));
    }

    // ── Skill recall ────────────────────────────────────────────────────────

    private static readonly Skill[] Candidates =
    [
        new("socialagent/recent-mentions", "Check recent social mentions within a time window", "body", DateTimeOffset.UtcNow),
        new("todo-live-operations", "Fetch, complete, rename or verify live Todo tasks", "body", DateTimeOffset.UtcNow),
        new("calendar-account-sweep", "Sweep every calendar account for events", "body", DateTimeOffset.UtcNow),
    ];

    [TestMethod]
    public async Task SkillRecall_ConfidentPick_PromotesALowerRank()
    {
        var decisions = new FakeDecisionService(choice: "todo-live-operations");

        Assert.AreEqual(1, await new SkillRecallPicker(decisions).PickAsync("mark the propane task complete", Candidates));
        var question = (ChoiceQuestion)decisions.Requests.Single().Questions[SkillRecallPicker.QuestionName];
        CollectionAssert.Contains(question.Options.Keys.ToList(), SkillRecallPicker.None);
        Assert.AreEqual("Fetch, complete, rename or verify live Todo tasks", question.Options["todo-live-operations"]);
    }

    [TestMethod]
    public async Task SkillRecall_None_InjectsNoFullBody()
    {
        Assert.AreEqual(-1,
            await new SkillRecallPicker(new FakeDecisionService(choice: SkillRecallPicker.None)).PickAsync("tell me a joke", Candidates));
    }

    [TestMethod]
    public async Task SkillRecall_UnsureUnknownOrUnavailable_KeepsRankOne()
    {
        var unsure = new FakeDecisionService(choice: "calendar-account-sweep");
        unsure.Thresholds[DecisionPurposes.SkillRecall] = 0.95; // the fake answers with confidence 0.9

        Assert.AreEqual(0, await new SkillRecallPicker(unsure).PickAsync("x", Candidates));
        Assert.AreEqual(0, await new SkillRecallPicker(new FakeDecisionService(choice: "not-a-skill")).PickAsync("x", Candidates));
        Assert.AreEqual(0, await new SkillRecallPicker(new FakeDecisionService(null)).PickAsync("x", Candidates));
        Assert.AreEqual(0, await new SkillRecallPicker().PickAsync("x", Candidates));
        Assert.AreEqual(-1, await new SkillRecallPicker().PickAsync("x", []), "nothing recalled");
    }

    // ── Completion check cache (first response, then the background loop) ──

    [TestMethod]
    public async Task CompletionCheck_SameReplyAskedOnce()
    {
        var decisions = new FakeDecisionService(byQuestion: new Dictionary<string, double>
        {
            [CompletionCheckClassifier.Promise] = 0.9,
            [CompletionCheckClassifier.UnbackedClaim] = 0.01,
            [CompletionCheckClassifier.Denial] = 0.01,
        });
        var classifier = new CompletionCheckClassifier(decisions);

        var first = await classifier.SecondOpinionAsync("Pulling the numbers together now.", "get me Q3", []);
        var second = await classifier.SecondOpinionAsync("Pulling the numbers together now.", "get me Q3", []);

        Assert.AreEqual(CompletionEvalTrigger.PromiseNoAction, first);
        Assert.AreEqual(first, second);
        Assert.AreEqual(1, decisions.Requests.Count);
    }
}

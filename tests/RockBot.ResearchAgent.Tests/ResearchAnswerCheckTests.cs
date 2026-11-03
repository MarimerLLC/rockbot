using RockBot.Llm;

namespace RockBot.ResearchAgent.Tests;

/// <summary>The decision-model check of a quick research answer (<see cref="ResearchAnswerCheck"/>).</summary>
[TestClass]
public class ResearchAnswerCheckTests
{
    /// <summary>Answers each question by name; a null map makes the call fail (returns null).</summary>
    private sealed class Decisions(IReadOnlyDictionary<string, double>? answers, bool enabled = true) : IDecisionService
    {
        public List<DecisionRequest> Requests { get; } = [];
        public double? Threshold { get; init; }

        public bool IsEnabled(string purpose) => enabled && purpose == DecisionPurposes.ResearchAnswer;

        public double ThresholdFor(string purpose, double fallback) => Threshold ?? fallback;

        public Task<DecisionResult?> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(answers is null
                ? null
                : new DecisionResult(answers.ToDictionary(a => a.Key, a => new DecisionAnswer(Probability: a.Value))));
        }
    }

    private static readonly ResearchRequest Request =
        new("Is hunting deer a net good for the environment?", Interpretation: "ecological effects", Depth: ResearchDepth.Quick);

    private static Dictionary<string, double> Answer(double misses, double tooLong) => new()
    {
        [ResearchAnswerCheck.MissesQuestion] = misses,
        [ResearchAnswerCheck.TooLong] = tooLong,
    };

    [TestMethod]
    public async Task AGoodAnswer_IsLeftAlone()
    {
        var check = new ResearchAnswerCheck(new Decisions(Answer(0.05, 0.10)));

        var verdict = await check.CheckAsync(Request, "A few short paragraphs.", CancellationToken.None);

        Assert.IsFalse(verdict.Rewrite);
        Assert.IsNull(verdict.Guidance);
    }

    [TestMethod]
    public async Task ALongAnswer_IsRewrittenShorter()
    {
        var check = new ResearchAnswerCheck(new Decisions(Answer(0.05, 0.92)));

        var verdict = await check.CheckAsync(Request, new string('x', 4595), CancellationToken.None);

        Assert.IsTrue(verdict.Rewrite);
        StringAssert.Contains(verdict.Guidance, "few short paragraphs");
        StringAssert.Contains(verdict.Guidance, "Do not call any tools.");
        Assert.IsFalse(verdict.Guidance!.Contains("Answer the question directly"));
    }

    [TestMethod]
    public async Task AnAnswerThatMissesTheQuestion_IsToldToAnswerIt()
    {
        var check = new ResearchAnswerCheck(new Decisions(Answer(0.81, 0.92)));

        var verdict = await check.CheckAsync(Request, "I would look into deer populations.", CancellationToken.None);

        Assert.IsTrue(verdict.Rewrite);
        StringAssert.Contains(verdict.Guidance, "Answer the question directly");
        StringAssert.Contains(verdict.Guidance, "few short paragraphs");
    }

    [TestMethod]
    public async Task TheThreshold_IsConfigurable()
    {
        var check = new ResearchAnswerCheck(new Decisions(Answer(0.05, 0.6)) { Threshold = 0.7 });

        var verdict = await check.CheckAsync(Request, "Medium length.", CancellationToken.None);

        Assert.IsFalse(verdict.Rewrite, "0.6 is under the configured 0.7");
    }

    [TestMethod]
    public async Task AFailedCall_KeepsTheAnswer()
    {
        var check = new ResearchAnswerCheck(new Decisions(answers: null));

        var verdict = await check.CheckAsync(Request, "Anything.", CancellationToken.None);

        Assert.IsFalse(verdict.Rewrite);
    }

    [TestMethod]
    public async Task WithoutADecisionModel_TheCheckIsOff_AndAsksNothing()
    {
        var decisions = new Decisions(Answer(0.9, 0.9), enabled: false);
        var check = new ResearchAnswerCheck(decisions);

        Assert.IsFalse(check.IsEnabled);
        Assert.IsFalse(new ResearchAnswerCheck().IsEnabled, "no decision service at all");
        var verdict = await check.CheckAsync(Request, "Anything.", CancellationToken.None);

        Assert.IsFalse(verdict.Rewrite);
        Assert.AreEqual(0, decisions.Requests.Count);
    }

    [TestMethod]
    public void TheRequest_CarriesTheQuestionTheReadingAndTheLength()
    {
        var request = ResearchAnswerCheck.BuildRequest(Request, new string('y', 9000));

        Assert.AreEqual(DecisionPurposes.ResearchAnswer, request.Purpose);
        CollectionAssert.AreEquivalent(
            new[] { ResearchAnswerCheck.MissesQuestion, ResearchAnswerCheck.TooLong }, request.Questions.Keys.ToArray());
        var state = (IDictionary<string, object?>)request.State;
        Assert.AreEqual(Request.Question, state["research_question"]);
        Assert.AreEqual("ecological effects", state["interpretation_to_research"]);
        Assert.AreEqual(9000, state["answer_length_chars"], "the true length, not the truncated one");
        Assert.IsTrue(((string)state["answer"]!).Length < 9000, "a long answer is truncated in the state");
    }
}

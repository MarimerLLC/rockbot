using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Observation.Tests;

[TestClass]
public class LlmObservationEvaluatorTests
{
    private static ObservationTarget MakeTarget() => new()
    {
        Name = "test",
        Filter = new PassThrough(),
        ExtractionPrompt = "x",
        EvaluationPrompt = "Evaluate.",
        StateFilePath = "/tmp/x.json",
        OutputMarkdownPath = "/tmp/x.md",
    };

    private static Candidate MakeCandidate(string id, string text = "claim", int count = 3)
    {
        return new Candidate
        {
            Id = id,
            Text = text,
            ClusterId = "c1",
            Count = count,
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
            References =
            {
                new ObservationReference("conv1", "t1", "supporting quote here", DateTimeOffset.UtcNow),
            },
        };
    }

    [TestMethod]
    public async Task EvaluateAsync_NoEligibleCandidates_DoesNotCallLlm()
    {
        var stub = new EvaluatorLlm();
        var evaluator = new LlmObservationEvaluator(stub, NullLogger<LlmObservationEvaluator>.Instance);

        var verdicts = await evaluator.EvaluateAsync(MakeTarget(), [], [], CancellationToken.None);

        Assert.AreEqual(0, verdicts.Count);
        Assert.AreEqual(0, stub.CallCount);
    }

    [TestMethod]
    public async Task EvaluateAsync_ParsesPromoteRefineRejectVerdicts()
    {
        var stub = new EvaluatorLlm
        {
            Response =
                """
                {
                  "verdicts": [
                    { "candidateId": "cand_1", "action": "promote", "reason": "grounded" },
                    { "candidateId": "cand_2", "action": "refine", "refinedText": "rephrased" },
                    { "candidateId": "cand_3", "action": "reject", "reason": "noisy" }
                  ]
                }
                """
        };
        var evaluator = new LlmObservationEvaluator(stub, NullLogger<LlmObservationEvaluator>.Instance);

        var candidates = new[]
        {
            MakeCandidate("cand_1"),
            MakeCandidate("cand_2"),
            MakeCandidate("cand_3"),
        };

        var verdicts = await evaluator.EvaluateAsync(MakeTarget(), candidates, [], CancellationToken.None);

        Assert.AreEqual(3, verdicts.Count);
        Assert.AreEqual(EvaluationAction.Promote, verdicts.Single(v => v.CandidateId == "cand_1").Action);
        Assert.AreEqual(EvaluationAction.Refine, verdicts.Single(v => v.CandidateId == "cand_2").Action);
        Assert.AreEqual("rephrased", verdicts.Single(v => v.CandidateId == "cand_2").RefinedText);
        Assert.AreEqual(EvaluationAction.Reject, verdicts.Single(v => v.CandidateId == "cand_3").Action);
    }

    [TestMethod]
    public async Task EvaluateAsync_UnrecognisedAction_BecomesUnspecified()
    {
        var stub = new EvaluatorLlm
        {
            Response =
                """{ "verdicts": [{ "candidateId": "cand_1", "action": "burninate" }] }"""
        };
        var evaluator = new LlmObservationEvaluator(stub, NullLogger<LlmObservationEvaluator>.Instance);

        var verdicts = await evaluator.EvaluateAsync(
            MakeTarget(), new[] { MakeCandidate("cand_1") }, [], CancellationToken.None);

        Assert.AreEqual(1, verdicts.Count);
        Assert.AreEqual(EvaluationAction.Unspecified, verdicts[0].Action);
    }

    [TestMethod]
    public async Task EvaluateAsync_LlmThrows_ReturnsEmpty()
    {
        var stub = new EvaluatorLlm { ThrowOnCall = true };
        var evaluator = new LlmObservationEvaluator(stub, NullLogger<LlmObservationEvaluator>.Instance);

        var verdicts = await evaluator.EvaluateAsync(
            MakeTarget(), new[] { MakeCandidate("cand_1") }, [], CancellationToken.None);

        Assert.AreEqual(0, verdicts.Count);
    }

    [TestMethod]
    public async Task EvaluateAsync_MalformedJson_ReturnsEmpty()
    {
        var stub = new EvaluatorLlm { Response = "{ this isn't json" };
        var evaluator = new LlmObservationEvaluator(stub, NullLogger<LlmObservationEvaluator>.Instance);

        var verdicts = await evaluator.EvaluateAsync(
            MakeTarget(), new[] { MakeCandidate("cand_1") }, [], CancellationToken.None);

        Assert.AreEqual(0, verdicts.Count);
    }

    [TestMethod]
    public async Task EvaluateAsync_Cancelled_Throws()
    {
        var stub = new EvaluatorLlm();
        var evaluator = new LlmObservationEvaluator(stub, NullLogger<LlmObservationEvaluator>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await evaluator.EvaluateAsync(
                MakeTarget(), new[] { MakeCandidate("cand_1") }, [], cts.Token));
    }

    [TestMethod]
    public async Task EvaluateAsync_DropsVerdictsWithMissingCandidateId()
    {
        var stub = new EvaluatorLlm
        {
            Response =
                """
                {
                  "verdicts": [
                    { "action": "promote" },
                    { "candidateId": "", "action": "promote" },
                    { "candidateId": "cand_1", "action": "promote" }
                  ]
                }
                """
        };
        var evaluator = new LlmObservationEvaluator(stub, NullLogger<LlmObservationEvaluator>.Instance);

        var verdicts = await evaluator.EvaluateAsync(
            MakeTarget(), new[] { MakeCandidate("cand_1") }, [], CancellationToken.None);

        Assert.AreEqual(1, verdicts.Count);
        Assert.AreEqual("cand_1", verdicts[0].CandidateId);
    }

    private sealed class PassThrough : ITranscriptFilter
    {
        public IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns) => turns;
    }

    /// <summary>Tiny ILlmClient stub for evaluator-only tests.</summary>
    private sealed class EvaluatorLlm : RockBot.Host.ILlmClient
    {
        public string Response { get; set; } = """{ "verdicts": [] }""";
        public bool ThrowOnCall { get; set; }
        public int CallCount { get; private set; }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options,
            CancellationToken cancellationToken)
            => GetResponseAsync(messages, RockBot.Host.ModelTier.Balanced, options, cancellationToken);

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            RockBot.Host.ModelTier tier,
            Microsoft.Extensions.AI.ChatOptions? options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (ThrowOnCall) throw new InvalidOperationException("simulated");
            return Task.FromResult(new Microsoft.Extensions.AI.ChatResponse(
                new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.Assistant, Response)));
        }
    }
}

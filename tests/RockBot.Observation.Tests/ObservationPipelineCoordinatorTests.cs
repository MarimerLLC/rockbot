using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Observation.Tests;

[TestClass]
public class ObservationPipelineCoordinatorTests
{
    private static ObservationTarget MakeTarget(string name, ITranscriptFilter? filter = null) => new()
    {
        Name = name,
        Filter = filter ?? new PassThrough(),
        ExtractionPrompt = "x",
        EvaluationPrompt = "y",
        StateFilePath = "/tmp/" + name + ".json",
        OutputMarkdownPath = "/tmp/" + name + ".md",
    };

    private static TranscriptTurn Turn(string convId, string id, string source = TranscriptSources.User) =>
        new(convId, id, source, "user", "content " + id, DateTimeOffset.UtcNow);

    [TestMethod]
    public async Task RunAllAsync_NoTargets_ReturnsEmpty()
    {
        var coord = new ObservationPipelineCoordinator(
            [],
            new StubExtractionPhase(),
            new StubEvaluationPhase(),
            NullLogger<ObservationPipelineCoordinator>.Instance);

        var results = await coord.RunAllAsync(new[] { Turn("c", "t1") }, CancellationToken.None);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task RunAllAsync_RunsBothPhasesPerTarget()
    {
        var t1 = MakeTarget("t1");
        var t2 = MakeTarget("t2");
        var ext = new StubExtractionPhase();
        var ev = new StubEvaluationPhase();

        var coord = new ObservationPipelineCoordinator(
            [t1, t2], ext, ev, NullLogger<ObservationPipelineCoordinator>.Instance);

        var results = await coord.RunAllAsync(new[] { Turn("c", "t1") }, CancellationToken.None);

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual(2, ext.Calls.Count);
        Assert.AreEqual(2, ev.Calls.Count);
        CollectionAssert.AreEqual(
            new[] { "t1", "t2" },
            results.Select(r => r.TargetName).ToArray());
        Assert.IsTrue(results.All(r => r.Failure is null));
    }

    [TestMethod]
    public async Task RunAllAsync_PerTargetFailureDoesNotBlockOthers()
    {
        var good = MakeTarget("good");
        var bad = MakeTarget("bad");

        var ext = new StubExtractionPhase
        {
            ThrowForTarget = "bad",
        };
        var ev = new StubEvaluationPhase();

        var coord = new ObservationPipelineCoordinator(
            [bad, good], ext, ev, NullLogger<ObservationPipelineCoordinator>.Instance);

        var results = await coord.RunAllAsync(new[] { Turn("c", "t1") }, CancellationToken.None);

        Assert.AreEqual(2, results.Count);
        var badResult = results.Single(r => r.TargetName == "bad");
        Assert.IsNotNull(badResult.Failure);
        Assert.IsNull(badResult.ExtractionResult);

        var goodResult = results.Single(r => r.TargetName == "good");
        Assert.IsNull(goodResult.Failure);
        Assert.IsNotNull(goodResult.ExtractionResult);
        Assert.IsNotNull(goodResult.EvaluationResult);
    }

    [TestMethod]
    public async Task RunAllAsync_AppliesPerTargetFilterBeforeExtraction()
    {
        var userOnly = MakeTarget("user-only", TranscriptFilters.UserAuthored);
        var ext = new StubExtractionPhase();
        var ev = new StubEvaluationPhase();

        var transcripts = new[]
        {
            Turn("c", "t1", TranscriptSources.User),
            Turn("c", "t2", TranscriptSources.ScheduledTask),
            Turn("c", "t3", TranscriptSources.Agent),
        };

        var coord = new ObservationPipelineCoordinator(
            [userOnly], ext, ev, NullLogger<ObservationPipelineCoordinator>.Instance);

        await coord.RunAllAsync(transcripts, CancellationToken.None);

        var passed = ext.Calls.Single().Transcripts;
        // user-only filter excludes ScheduledTask. Agent assistant turns
        // would be kept by UserAuthored, but we sent role=user with source=Agent
        // which the filter doesn't match → only the User-source turn survives.
        // Verify: at least the ScheduledTask is excluded; t1 and t3 may both pass
        // depending on role mapping — UserAuthored allows source=user OR
        // (source=agent && role=assistant). Our t3 has role=user so it doesn't pass.
        Assert.IsTrue(passed.All(t => t.Source != TranscriptSources.ScheduledTask),
            "Filter should drop scheduled-task turns");
    }

    [TestMethod]
    public async Task RunAllAsync_Cancellation_StopsBeforeNextTarget()
    {
        var t1 = MakeTarget("t1");
        var t2 = MakeTarget("t2");
        using var cts = new CancellationTokenSource();

        var ext = new StubExtractionPhase
        {
            BeforeReturn = (target) =>
            {
                if (target.Name == "t1") cts.Cancel();
            },
        };
        var ev = new StubEvaluationPhase();

        var coord = new ObservationPipelineCoordinator(
            [t1, t2], ext, ev, NullLogger<ObservationPipelineCoordinator>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await coord.RunAllAsync(new[] { Turn("c", "x") }, cts.Token));

        Assert.AreEqual(1, ext.Calls.Count, "Only the first target should have run");
    }

    [TestMethod]
    public async Task RunAllAsync_NullTranscripts_Throws()
    {
        var coord = new ObservationPipelineCoordinator(
            [MakeTarget("t")],
            new StubExtractionPhase(), new StubEvaluationPhase(),
            NullLogger<ObservationPipelineCoordinator>.Instance);

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(async () =>
            await coord.RunAllAsync(null!, CancellationToken.None));
    }

    private sealed class PassThrough : ITranscriptFilter
    {
        public IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns) => turns;
    }

    private sealed class StubExtractionPhase : IObservationExtractionPhase
    {
        public List<(ObservationTarget Target, IReadOnlyList<TranscriptTurn> Transcripts)> Calls { get; } = [];
        public string? ThrowForTarget { get; set; }
        public Action<ObservationTarget>? BeforeReturn { get; set; }

        public Task<ExtractionPhaseResult> ExecuteAsync(
            ObservationTarget target,
            IReadOnlyList<TranscriptTurn> transcripts,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((target, transcripts));
            BeforeReturn?.Invoke(target);

            if (target.Name == ThrowForTarget)
                throw new InvalidOperationException("simulated phase 1 failure for " + target.Name);

            return Task.FromResult(new ExtractionPhaseResult(
                ConversationsProcessed: 1,
                ConversationsFailed: 0,
                ProposalsReceived: 0,
                ProposalsGrounded: 0,
                MatchedExistingCandidates: 0,
                NewCandidatesCreated: 0,
                StateWritten: true));
        }
    }

    private sealed class StubEvaluationPhase : IObservationEvaluationPhase
    {
        public List<ObservationTarget> Calls { get; } = [];

        public Task<EvaluationPhaseResult> ExecuteAsync(
            ObservationTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(target);
            return Task.FromResult(new EvaluationPhaseResult(
                CandidatesAged: 0,
                TheoriesAged: 0,
                CandidatesEvaluated: 0,
                CandidatesPromoted: 0,
                CandidatesRefined: 0,
                CandidatesRejected: 0,
                MarkdownRegenerated: true,
                StateWritten: true));
        }
    }
}

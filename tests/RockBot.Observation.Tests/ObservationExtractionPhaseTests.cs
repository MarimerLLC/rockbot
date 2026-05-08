using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Observation.Tests;

[TestClass]
public class ObservationExtractionPhaseTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "rockbot-observation-phase-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private ObservationTarget MakeTarget(
        string name = "test",
        float similarity = 0.85f) => new()
    {
        Name = name,
        Filter = new PassThrough(),
        ExtractionPrompt = "Extract.",
        EvaluationPrompt = "Evaluate.",
        StateFilePath = Path.Combine(_tempDir, $"{name}.json"),
        OutputMarkdownPath = Path.Combine(_tempDir, $"{name}.md"),
        ClusteringSimilarityThreshold = similarity,
    };

    private ObservationExtractionPhase MakePhase(
        StubLlmClient llm,
        StubEmbeddingGenerator embeddings) =>
        new(
            new LlmObservationExtractor(llm, NullLogger<LlmObservationExtractor>.Instance),
            embeddings,
            new FileObservationStateStore(NullLogger<FileObservationStateStore>.Instance),
            NullLogger<ObservationExtractionPhase>.Instance);

    private static IReadOnlyList<TranscriptTurn> Turns(string convId, params (string Id, string Content)[] turns) =>
        turns.Select(t => new TranscriptTurn(convId, t.Id, "user", "user", t.Content, DateTimeOffset.UtcNow))
             .ToArray();

    private static string ResponseWith(params (string Text, string ConvId, string TurnId, string Quote)[] obs)
    {
        var items = obs.Select(o =>
            $$"""{ "text": "{{o.Text}}", "conversationId": "{{o.ConvId}}", "turnId": "{{o.TurnId}}", "quote": "{{o.Quote}}" }""");
        return $$"""{ "observations": [ {{string.Join(", ", items)}} ] }""";
    }

    [TestMethod]
    public async Task ExecuteAsync_NoTranscripts_NoOpAndNoStateWrite()
    {
        var llm = new StubLlmClient();
        var emb = new StubEmbeddingGenerator();
        var phase = MakePhase(llm, emb);
        var target = MakeTarget();

        var result = await phase.ExecuteAsync(target, [], CancellationToken.None);

        Assert.IsFalse(result.StateWritten);
        Assert.AreEqual(0, result.ConversationsProcessed);
        Assert.AreEqual(0, llm.CallCount);
        Assert.IsFalse(File.Exists(target.StateFilePath));
    }

    [TestMethod]
    public async Task ExecuteAsync_SingleConversation_SingleObservation_CreatesCandidate()
    {
        var llm = new StubLlmClient().AddResponse("conv1",
            ResponseWith(("User likes terse responses", "conv1", "t1", "prefer terse responses")));
        var emb = new StubEmbeddingGenerator()
            .Category("terse-cluster", "User likes terse responses");

        var phase = MakePhase(llm, emb);
        var target = MakeTarget();
        var transcripts = Turns("conv1", ("t1", "I prefer terse responses, no trailing summaries please."));

        var result = await phase.ExecuteAsync(target, transcripts, CancellationToken.None);

        Assert.IsTrue(result.StateWritten);
        Assert.AreEqual(1, result.NewCandidatesCreated);
        Assert.AreEqual(0, result.MatchedExistingCandidates);
        Assert.AreEqual(1, result.ProposalsReceived);
        Assert.AreEqual(1, result.ProposalsGrounded);

        var state = await new FileObservationStateStore(NullLogger<FileObservationStateStore>.Instance)
            .LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(1, state.Candidates.Count);
        Assert.AreEqual(1, state.Candidates[0].Count);
        Assert.AreEqual(1, state.Candidates[0].References.Count);
        Assert.IsNotNull(state.Candidates[0].Vector);
        Assert.IsNotNull(state.LastDreamAt);
    }

    [TestMethod]
    public async Task ExecuteAsync_TwoSimilarObservations_MergeIntoOneCandidate()
    {
        // Same cluster category, two different conversations → one candidate, count=2.
        var llm = new StubLlmClient()
            .AddResponse("conv1", ResponseWith(("User likes terse responses", "conv1", "t1", "prefer terse responses")))
            .AddResponse("conv2", ResponseWith(("User dislikes long answers", "conv2", "t1", "stop padding")));
        var emb = new StubEmbeddingGenerator()
            .Category("brevity", "User likes terse responses", "User dislikes long answers");

        var phase = MakePhase(llm, emb);
        var target = MakeTarget(similarity: 0.9f);

        var transcripts = new List<TranscriptTurn>();
        transcripts.AddRange(Turns("conv1", ("t1", "I prefer terse responses please.")));
        transcripts.AddRange(Turns("conv2", ("t1", "Please stop padding everything.")));

        var result = await phase.ExecuteAsync(target, transcripts, CancellationToken.None);

        Assert.AreEqual(1, result.NewCandidatesCreated, "First obs creates candidate");
        Assert.AreEqual(1, result.MatchedExistingCandidates, "Second obs merges into the cluster");

        var state = await new FileObservationStateStore(NullLogger<FileObservationStateStore>.Instance)
            .LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(1, state.Candidates.Count);
        Assert.AreEqual(2, state.Candidates[0].Count, "Two distinct conversations contributing");
        Assert.AreEqual(2, state.Candidates[0].References.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_TwoUnrelatedObservations_TwoCandidates()
    {
        var llm = new StubLlmClient()
            .AddResponse("conv1", ResponseWith(("User likes terse responses", "conv1", "t1", "prefer terse responses")))
            .AddResponse("conv2", ResponseWith(("Agent over-explores tool calls", "conv2", "t1", "reading three files for")));
        var emb = new StubEmbeddingGenerator()
            .Category("brevity", "User likes terse responses")
            .Category("over-explore", "Agent over-explores tool calls");

        var phase = MakePhase(llm, emb);
        var target = MakeTarget();

        var transcripts = new List<TranscriptTurn>();
        transcripts.AddRange(Turns("conv1", ("t1", "I prefer terse responses please.")));
        transcripts.AddRange(Turns("conv2", ("t1", "you don't need to be reading three files for one edit.")));

        var result = await phase.ExecuteAsync(target, transcripts, CancellationToken.None);

        Assert.AreEqual(2, result.NewCandidatesCreated);
        Assert.AreEqual(0, result.MatchedExistingCandidates);

        var state = await new FileObservationStateStore(NullLogger<FileObservationStateStore>.Instance)
            .LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(2, state.Candidates.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_QuoteNotPresent_DropsObservation()
    {
        // LLM proposes an observation with a quote that's NOT in the source turn.
        var llm = new StubLlmClient().AddResponse("conv1",
            ResponseWith(("Hallucinated claim", "conv1", "t1", "this text is not in the turn at all")));
        var emb = new StubEmbeddingGenerator();
        var phase = MakePhase(llm, emb);
        var target = MakeTarget();

        var transcripts = Turns("conv1", ("t1", "Some entirely different content."));

        var result = await phase.ExecuteAsync(target, transcripts, CancellationToken.None);

        Assert.AreEqual(1, result.ProposalsReceived);
        Assert.AreEqual(0, result.ProposalsGrounded, "Quote-grounding must drop ungrounded proposals");
        Assert.AreEqual(0, result.NewCandidatesCreated);
    }

    [TestMethod]
    public async Task ExecuteAsync_OneConversationFails_OthersStillProcessed()
    {
        var llm = new StubLlmClient()
            .ThrowFor("conv1")
            .AddResponse("conv2",
                ResponseWith(("Claim from conv2", "conv2", "t1", "supporting quote here")));
        var emb = new StubEmbeddingGenerator()
            .Category("c2", "Claim from conv2");

        var phase = MakePhase(llm, emb);
        var target = MakeTarget();

        var transcripts = new List<TranscriptTurn>();
        transcripts.AddRange(Turns("conv1", ("t1", "anything")));
        transcripts.AddRange(Turns("conv2", ("t1", "supporting quote here directly.")));

        var result = await phase.ExecuteAsync(target, transcripts, CancellationToken.None);

        // The LlmObservationExtractor catches the LLM failure and returns empty;
        // it doesn't propagate up to the orchestrator, so ConversationsFailed
        // stays at 0 (the orchestrator's failure counter is for unhandled
        // exceptions only). conv1 simply contributes 0 proposals.
        Assert.AreEqual(2, result.ConversationsProcessed);
        Assert.AreEqual(1, result.NewCandidatesCreated);
    }

    [TestMethod]
    public async Task ExecuteAsync_AllConversationsFail_NoStateWrite()
    {
        // Force the orchestrator's outer catch to fire by making the EXTRACTOR
        // itself throw rather than the inner LLM. We do this by passing a
        // bespoke extractor that always throws.
        var emb = new StubEmbeddingGenerator();
        var phase = new ObservationExtractionPhase(
            new ThrowingExtractor(),
            emb,
            new FileObservationStateStore(NullLogger<FileObservationStateStore>.Instance),
            NullLogger<ObservationExtractionPhase>.Instance);

        var target = MakeTarget();
        var transcripts = new List<TranscriptTurn>();
        transcripts.AddRange(Turns("conv1", ("t1", "anything")));
        transcripts.AddRange(Turns("conv2", ("t1", "anything")));

        var result = await phase.ExecuteAsync(target, transcripts, CancellationToken.None);

        Assert.IsFalse(result.StateWritten, "All-fail batch must not write state");
        Assert.AreEqual(2, result.ConversationsFailed);
        Assert.IsFalse(File.Exists(target.StateFilePath));
    }

    [TestMethod]
    public async Task ExecuteAsync_ZeroGroundedProposals_StillUpdatesLastDreamAt()
    {
        // LLM returns proposals that all fail quote-grounding. State should
        // still be written with LastDreamAt updated so the next dream knows
        // this window has been processed.
        var llm = new StubLlmClient().AddResponse("conv1",
            ResponseWith(("Hallucinated", "conv1", "t1", "not in turn")));
        var emb = new StubEmbeddingGenerator();
        var phase = MakePhase(llm, emb);
        var target = MakeTarget();

        var transcripts = Turns("conv1", ("t1", "completely different content."));

        var result = await phase.ExecuteAsync(target, transcripts, CancellationToken.None);

        Assert.IsTrue(result.StateWritten);
        Assert.AreEqual(0, result.ProposalsGrounded);

        var state = await new FileObservationStateStore(NullLogger<FileObservationStateStore>.Instance)
            .LoadAsync(target, CancellationToken.None);
        Assert.IsNotNull(state.LastDreamAt);
        Assert.AreEqual(0, state.Candidates.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_Cancelled_DoesNotWriteState()
    {
        var llm = new StubLlmClient().AddResponse("conv1",
            ResponseWith(("Claim", "conv1", "t1", "supporting quote here")));
        var emb = new StubEmbeddingGenerator();
        var phase = MakePhase(llm, emb);
        var target = MakeTarget();

        var transcripts = Turns("conv1", ("t1", "supporting quote here in the turn."));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await phase.ExecuteAsync(target, transcripts, cts.Token));

        Assert.IsFalse(File.Exists(target.StateFilePath),
            "Cancellation before state write must leave no file behind");
    }

    [TestMethod]
    public async Task ExecuteAsync_PriorState_PreservesExistingCandidates()
    {
        var target = MakeTarget();

        // Seed an existing candidate in state
        var preexisting = new ObservationState
        {
            LastDreamAt = DateTimeOffset.UtcNow.AddDays(-1),
            Candidates =
            {
                new Candidate
                {
                    Id = "cand_existing",
                    Text = "Existing claim",
                    ClusterId = "clust_existing",
                    Count = 1,
                    FirstSeen = DateTimeOffset.UtcNow.AddDays(-5),
                    LastSeen = DateTimeOffset.UtcNow.AddDays(-5),
                    Vector = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                    References = { new ObservationReference("convOLD", "tOLD", "old quote",
                        DateTimeOffset.UtcNow.AddDays(-5)) },
                },
            },
        };
        await new FileObservationStateStore(NullLogger<FileObservationStateStore>.Instance)
            .SaveAsync(target, preexisting, CancellationToken.None);

        // Now run a phase that adds a new, unrelated candidate
        var llm = new StubLlmClient().AddResponse("conv1",
            ResponseWith(("Brand new claim", "conv1", "t1", "supporting quote here")));
        var emb = new StubEmbeddingGenerator()
            .Category("brand-new", "Brand new claim");

        var phase = MakePhase(llm, emb);
        var transcripts = Turns("conv1", ("t1", "supporting quote here in the turn."));

        var result = await phase.ExecuteAsync(target, transcripts, CancellationToken.None);

        Assert.AreEqual(1, result.NewCandidatesCreated);

        var state = await new FileObservationStateStore(NullLogger<FileObservationStateStore>.Instance)
            .LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(2, state.Candidates.Count, "Existing candidate must be preserved");
        Assert.IsTrue(state.Candidates.Any(c => c.Id == "cand_existing"));
    }

    [TestMethod]
    public async Task ExecuteAsync_SameConversationTwoObservationsToSameCluster_OneReferenceCount()
    {
        // Two observations from the SAME conversation that cluster together.
        // Count should reflect 1 distinct conversation, not 2.
        var llm = new StubLlmClient().AddResponse("conv1",
            ResponseWith(
                ("Brevity preference A", "conv1", "t1", "prefer terse responses"),
                ("Brevity preference B", "conv1", "t2", "stop padding everything")));
        var emb = new StubEmbeddingGenerator()
            .Category("brevity", "Brevity preference A", "Brevity preference B");

        var phase = MakePhase(llm, emb);
        var target = MakeTarget(similarity: 0.9f);
        var transcripts = Turns("conv1",
            ("t1", "I prefer terse responses please."),
            ("t2", "Please stop padding everything."));

        await phase.ExecuteAsync(target, transcripts, CancellationToken.None);

        var state = await new FileObservationStateStore(NullLogger<FileObservationStateStore>.Instance)
            .LoadAsync(target, CancellationToken.None);

        Assert.AreEqual(1, state.Candidates.Count);
        Assert.AreEqual(1, state.Candidates[0].Count,
            "Two refs from the same conversation must collapse to one for the count metric");
        Assert.AreEqual(2, state.Candidates[0].References.Count);
    }

    private sealed class PassThrough : ITranscriptFilter
    {
        public IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns) => turns;
    }

    private sealed class ThrowingExtractor : IObservationExtractor
    {
        public Task<IReadOnlyList<ProposedObservation>> ExtractAsync(
            ObservationTarget target,
            IReadOnlyList<TranscriptTurn> conversationTurns,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("simulated unhandled failure");
    }
}

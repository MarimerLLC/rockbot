using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Observation.Tests;

[TestClass]
public class ObservationEvaluationPhaseTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "rockbot-observation-eval-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private ObservationTarget MakeTarget(
        int promotionThreshold = 3,
        int candidateAgingDays = 7,
        int theoryAgingDays = 30,
        int snapshotRetention = 12) => new()
    {
        Name = "test-target",
        Filter = new PassThrough(),
        ExtractionPrompt = "x",
        EvaluationPrompt = "Evaluate.",
        StateFilePath = Path.Combine(_tempDir, "state.json"),
        OutputMarkdownPath = Path.Combine(_tempDir, "out.md"),
        PromotionThreshold = promotionThreshold,
        CandidateAgingWindowDays = candidateAgingDays,
        TheoryAgingWindowDays = theoryAgingDays,
        SnapshotRetentionCount = snapshotRetention,
    };

    private FileObservationStateStore Store() => new(NullLogger<FileObservationStateStore>.Instance);

    private ObservationEvaluationPhase MakePhase(StubEvaluator evaluator, StubLongTermMemory? memory = null) =>
        new(evaluator, Store(), memory ?? new StubLongTermMemory(),
            NullLogger<ObservationEvaluationPhase>.Instance);

    private static Candidate Candidate(
        string id, int distinctConvs, DateTimeOffset lastSeen, string text = "obs")
    {
        var c = new Candidate
        {
            Id = id, Text = text, ClusterId = "cluster_" + id,
            Count = distinctConvs,
            FirstSeen = lastSeen.AddDays(-10),
            LastSeen = lastSeen,
        };
        for (var i = 0; i < distinctConvs; i++)
            c.References.Add(new ObservationReference(
                $"conv{id}_{i}", "t1", $"quote {id} {i} content", lastSeen.AddDays(-i)));
        return c;
    }

    private static Theory Theory(string id, DateTimeOffset lastReinforced, string text = "thry")
    {
        var t = new Theory
        {
            Id = id, Text = text,
            PromotedAt = lastReinforced.AddDays(-30),
            LastReinforced = lastReinforced,
        };
        t.References.Add(new ObservationReference(
            $"conv_{id}", "t1", $"quote for {id} body", lastReinforced));
        return t;
    }

    [TestMethod]
    public async Task ExecuteAsync_NoEligibleCandidates_NoEvaluatorCallButStillRegens()
    {
        var target = MakeTarget(promotionThreshold: 3);
        await Store().SaveAsync(target, new ObservationState
        {
            Candidates = { Candidate("cand_1", distinctConvs: 1, DateTimeOffset.UtcNow) },
        }, CancellationToken.None);

        var stub = new StubEvaluator();
        var phase = MakePhase(stub);

        var result = await phase.ExecuteAsync(target, CancellationToken.None);

        Assert.AreEqual(0, stub.CallCount, "No eligible candidates → evaluator skipped");
        Assert.AreEqual(0, result.CandidatesEvaluated);
        Assert.IsTrue(result.MarkdownRegenerated);
        Assert.IsTrue(result.StateWritten);
        Assert.IsTrue(File.Exists(target.OutputMarkdownPath));
    }

    [TestMethod]
    public async Task ExecuteAsync_EligibleCandidatePromoted_BecomesTheory()
    {
        var target = MakeTarget(promotionThreshold: 3);
        var seedCandidate = Candidate("cand_1", distinctConvs: 4, DateTimeOffset.UtcNow);
        await Store().SaveAsync(target, new ObservationState
        {
            Candidates = { seedCandidate },
        }, CancellationToken.None);

        var stub = new StubEvaluator
        {
            Verdicts =
            {
                new EvaluationVerdict("cand_1", EvaluationAction.Promote,
                    RefinedText: null, Reason: "grounded"),
            },
        };

        var result = await MakePhase(stub).ExecuteAsync(target, CancellationToken.None);

        Assert.AreEqual(1, result.CandidatesPromoted);

        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(0, state.Candidates.Count, "Promoted candidate should be removed from pool");
        Assert.AreEqual(1, state.Theories.Count);
        Assert.AreEqual("obs", state.Theories[0].Text);
        Assert.AreEqual(1, state.Theories[0].SourceCandidateIds.Count);
        Assert.AreEqual("cand_1", state.Theories[0].SourceCandidateIds[0]);
        Assert.AreEqual(seedCandidate.References.Count, state.Theories[0].References.Count,
            "Theory should carry over the candidate's references");
    }

    [TestMethod]
    public async Task ExecuteAsync_PromoteWithRefinedText_UsesRefinedTextOnTheory()
    {
        var target = MakeTarget(promotionThreshold: 3);
        await Store().SaveAsync(target, new ObservationState
        {
            Candidates = { Candidate("cand_1", 3, DateTimeOffset.UtcNow, text: "rough wording") },
        }, CancellationToken.None);

        var stub = new StubEvaluator
        {
            Verdicts =
            {
                new EvaluationVerdict("cand_1", EvaluationAction.Promote,
                    RefinedText: "polished wording", Reason: null),
            },
        };

        await MakePhase(stub).ExecuteAsync(target, CancellationToken.None);

        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual("polished wording", state.Theories[0].Text);
    }

    [TestMethod]
    public async Task ExecuteAsync_RefineVerdict_UpdatesCandidateText_LeavesItInPool()
    {
        var target = MakeTarget(promotionThreshold: 3);
        await Store().SaveAsync(target, new ObservationState
        {
            Candidates = { Candidate("cand_1", 3, DateTimeOffset.UtcNow, text: "rough") },
        }, CancellationToken.None);

        var stub = new StubEvaluator
        {
            Verdicts =
            {
                new EvaluationVerdict("cand_1", EvaluationAction.Refine,
                    RefinedText: "polished", Reason: null),
            },
        };

        var result = await MakePhase(stub).ExecuteAsync(target, CancellationToken.None);

        Assert.AreEqual(1, result.CandidatesRefined);
        Assert.AreEqual(0, result.CandidatesPromoted);

        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(1, state.Candidates.Count);
        Assert.AreEqual("polished", state.Candidates[0].Text);
        Assert.AreEqual(0, state.Theories.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectVerdict_RemovesCandidate()
    {
        var target = MakeTarget(promotionThreshold: 3);
        await Store().SaveAsync(target, new ObservationState
        {
            Candidates =
            {
                Candidate("keep", 3, DateTimeOffset.UtcNow),
                Candidate("drop", 3, DateTimeOffset.UtcNow),
            },
        }, CancellationToken.None);

        var stub = new StubEvaluator
        {
            Verdicts =
            {
                new EvaluationVerdict("drop", EvaluationAction.Reject, null, "noisy"),
            },
        };

        var result = await MakePhase(stub).ExecuteAsync(target, CancellationToken.None);

        Assert.AreEqual(1, result.CandidatesRejected);

        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(1, state.Candidates.Count);
        Assert.AreEqual("keep", state.Candidates[0].Id);
    }

    [TestMethod]
    public async Task ExecuteAsync_UnspecifiedVerdict_LeavesCandidateAlone()
    {
        var target = MakeTarget(promotionThreshold: 3);
        await Store().SaveAsync(target, new ObservationState
        {
            Candidates = { Candidate("cand_1", 3, DateTimeOffset.UtcNow, text: "untouched") },
        }, CancellationToken.None);

        var stub = new StubEvaluator
        {
            Verdicts =
            {
                new EvaluationVerdict("cand_1", EvaluationAction.Unspecified, null, null),
            },
        };

        await MakePhase(stub).ExecuteAsync(target, CancellationToken.None);

        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(1, state.Candidates.Count);
        Assert.AreEqual("untouched", state.Candidates[0].Text);
        Assert.AreEqual(0, state.Theories.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_AgesOldCandidates()
    {
        var target = MakeTarget(candidateAgingDays: 7);
        var now = DateTimeOffset.UtcNow;

        await Store().SaveAsync(target, new ObservationState
        {
            Candidates =
            {
                Candidate("fresh", 1, now.AddDays(-1)),
                Candidate("stale", 1, now.AddDays(-30)),
            },
        }, CancellationToken.None);

        var result = await MakePhase(new StubEvaluator()).ExecuteAsync(target, CancellationToken.None);

        Assert.AreEqual(1, result.CandidatesAged);

        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(1, state.Candidates.Count);
        Assert.AreEqual("fresh", state.Candidates[0].Id);
    }

    [TestMethod]
    public async Task ExecuteAsync_AgesOldTheories()
    {
        var target = MakeTarget(theoryAgingDays: 30);
        var now = DateTimeOffset.UtcNow;

        await Store().SaveAsync(target, new ObservationState
        {
            Theories =
            {
                Theory("fresh", now.AddDays(-5)),
                Theory("stale", now.AddDays(-90)),
            },
        }, CancellationToken.None);

        var result = await MakePhase(new StubEvaluator()).ExecuteAsync(target, CancellationToken.None);

        Assert.AreEqual(1, result.TheoriesAged);

        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(1, state.Theories.Count);
        Assert.AreEqual("fresh", state.Theories[0].Id);
    }

    [TestMethod]
    public async Task ExecuteAsync_AppendsSnapshot_RespectsCap()
    {
        var target = MakeTarget(snapshotRetention: 3);
        await Store().SaveAsync(target, new ObservationState
        {
            Snapshots =
            {
                new Snapshot(DateTimeOffset.UtcNow.AddDays(-3), "old1"),
                new Snapshot(DateTimeOffset.UtcNow.AddDays(-2), "old2"),
                new Snapshot(DateTimeOffset.UtcNow.AddDays(-1), "old3"),
            },
        }, CancellationToken.None);

        await MakePhase(new StubEvaluator()).ExecuteAsync(target, CancellationToken.None);

        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(3, state.Snapshots.Count, "Snapshots cap should hold");
        // Oldest evicted
        Assert.IsFalse(state.Snapshots.Any(s => s.Markdown == "old1"));
        // The newest is the just-rendered markdown
        Assert.IsTrue(state.Snapshots[2].Markdown.Contains("# Test target"));
    }

    [TestMethod]
    public async Task ExecuteAsync_MarkdownFileMatchesRenderedContent()
    {
        var target = MakeTarget();
        await Store().SaveAsync(target, new ObservationState
        {
            Theories = { Theory("t1", DateTimeOffset.UtcNow, text: "Some theory") },
        }, CancellationToken.None);

        await MakePhase(new StubEvaluator()).ExecuteAsync(target, CancellationToken.None);

        var written = await File.ReadAllTextAsync(target.OutputMarkdownPath);
        StringAssert.Contains(written, "Some theory");
        StringAssert.Contains(written, "# Test target");
    }

    [TestMethod]
    public async Task ExecuteAsync_Cancelled_LeavesStateAndMarkdownUnchanged()
    {
        var target = MakeTarget(promotionThreshold: 3);
        await Store().SaveAsync(target, new ObservationState
        {
            Candidates = { Candidate("cand_1", 3, DateTimeOffset.UtcNow) },
        }, CancellationToken.None);

        var stub = new StubEvaluator { ThrowOnCancel = true };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await MakePhase(stub).ExecuteAsync(target, cts.Token));

        // No markdown file should have been written
        Assert.IsFalse(File.Exists(target.OutputMarkdownPath));

        // State file is unchanged from the seed
        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(1, state.Candidates.Count);
        Assert.AreEqual(0, state.Theories.Count);
    }

    [TestMethod]
    public async Task ExecuteAsync_VerdictForUnknownCandidate_Ignored()
    {
        var target = MakeTarget(promotionThreshold: 3);
        await Store().SaveAsync(target, new ObservationState
        {
            Candidates = { Candidate("real", 3, DateTimeOffset.UtcNow) },
        }, CancellationToken.None);

        var stub = new StubEvaluator
        {
            Verdicts =
            {
                new EvaluationVerdict("ghost", EvaluationAction.Promote, null, null),
                new EvaluationVerdict("real", EvaluationAction.Promote, null, null),
            },
        };

        var result = await MakePhase(stub).ExecuteAsync(target, CancellationToken.None);

        Assert.AreEqual(1, result.CandidatesPromoted, "Verdict for non-existent candidate should be ignored");
    }

    [TestMethod]
    public async Task ExecuteAsync_NoStateFile_ProducesEmptyMarkdownAndEmptyState()
    {
        var target = MakeTarget();

        var result = await MakePhase(new StubEvaluator()).ExecuteAsync(target, CancellationToken.None);

        Assert.IsTrue(result.MarkdownRegenerated);
        var written = await File.ReadAllTextAsync(target.OutputMarkdownPath);
        StringAssert.Contains(written, "## Theories (0)");
        StringAssert.Contains(written, "## Candidate observations (0)");
    }

    private sealed class PassThrough : ITranscriptFilter
    {
        public IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns) => turns;
    }

    private sealed class StubEvaluator : IObservationEvaluator
    {
        public List<EvaluationVerdict> Verdicts { get; } = [];
        public bool ThrowOnCancel { get; set; }
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<EvaluationVerdict>> EvaluateAsync(
            ObservationTarget target,
            IReadOnlyList<Candidate> eligibleCandidates,
            IReadOnlyList<Theory> existingTheories,
            CancellationToken cancellationToken)
        {
            if (ThrowOnCancel)
                cancellationToken.ThrowIfCancellationRequested();

            CallCount++;
            return Task.FromResult<IReadOnlyList<EvaluationVerdict>>(Verdicts);
        }
    }

    /// <summary>
    /// Minimal in-memory <see cref="ILongTermMemory"/> stub that records
    /// saves and deletes for verification. Optionally throws on save/delete
    /// to exercise the framework's best-effort error handling.
    /// </summary>
    internal sealed class StubLongTermMemory : RockBot.Host.ILongTermMemory
    {
        public Dictionary<string, RockBot.Host.MemoryEntry> Saved { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = [];
        public List<(string Id, string Reason)> Archived { get; } = [];
        public bool ThrowOnSave { get; set; }
        public bool ThrowOnDelete { get; set; }

        public Task SaveAsync(RockBot.Host.MemoryEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnSave) throw new InvalidOperationException("simulated memory save failure");
            Saved[entry.Id] = entry;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnDelete) throw new InvalidOperationException("simulated memory delete failure");
            Deleted.Add(id);
            Saved.Remove(id);
            return Task.CompletedTask;
        }

        public Task ArchiveAsync(string id, string reason, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnDelete) throw new InvalidOperationException("simulated memory archive failure");
            Archived.Add((id, reason));
            Saved.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RockBot.Host.MemoryEntry>> SearchAsync(
            RockBot.Host.MemorySearchCriteria criteria, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RockBot.Host.MemoryEntry>>(Saved.Values.ToList());

        public Task<RockBot.Host.MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(Saved.TryGetValue(id, out var entry) ? entry : null);

        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(
                Saved.Values.SelectMany(e => e.Tags).Distinct().ToList());

        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(
                Saved.Values.Select(e => e.Category).Where(c => c is not null).Distinct().ToList()!);
    }

    [TestMethod]
    public async Task ExecuteAsync_PromotedTheoryPublishedToMemory()
    {
        var target = MakeTarget(promotionThreshold: 3);
        await Store().SaveAsync(target, new ObservationState
        {
            Candidates = { Candidate("cand_1", 3, DateTimeOffset.UtcNow, text: "User prefers terse responses") },
        }, CancellationToken.None);

        var stubEval = new StubEvaluator
        {
            Verdicts =
            {
                new EvaluationVerdict("cand_1", EvaluationAction.Promote, null, null),
            },
        };
        var stubMemory = new StubLongTermMemory();

        await MakePhase(stubEval, stubMemory).ExecuteAsync(target, CancellationToken.None);

        Assert.AreEqual(1, stubMemory.Saved.Count, "Promoted theory should be published to long-term memory");
        var entry = stubMemory.Saved.Values.Single();
        Assert.AreEqual("User prefers terse responses", entry.Content);
        Assert.AreEqual($"observation/theory/{target.Name}", entry.Category);
        CollectionAssert.AreEquivalent(new[] { "observation", target.Name }, entry.Tags.ToArray());
        Assert.IsTrue(entry.ImportanceScore > 0.5f, "Theories carry above-default importance");

        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(entry.Id, state.Theories[0].MemoryEntryId,
            "Theory should record the published memory entry's ID");
    }

    [TestMethod]
    public async Task ExecuteAsync_AgedTheoryArchivedNotDeleted()
    {
        var target = MakeTarget(theoryAgingDays: 30);
        var now = DateTimeOffset.UtcNow;

        var stale = Theory("stale", now.AddDays(-90));
        stale.MemoryEntryId = "obs_existing_stale";
        var fresh = Theory("fresh", now.AddDays(-5));
        fresh.MemoryEntryId = "obs_existing_fresh";

        await Store().SaveAsync(target, new ObservationState
        {
            Theories = { stale, fresh },
        }, CancellationToken.None);

        var stubMemory = new StubLongTermMemory();
        // Pretend the entries existed in memory before this dream
        stubMemory.Saved["obs_existing_stale"] = new RockBot.Host.MemoryEntry(
            "obs_existing_stale", "stale", null, [], now.AddDays(-90));
        stubMemory.Saved["obs_existing_fresh"] = new RockBot.Host.MemoryEntry(
            "obs_existing_fresh", "fresh", null, [], now.AddDays(-5));

        await MakePhase(new StubEvaluator(), stubMemory).ExecuteAsync(target, CancellationToken.None);

        // Archived rather than deleted: a theory ages out because it stopped being re-observed in
        // a window, which is weak evidence that it was wrong — the behaviour may simply not have
        // come up.
        Assert.AreEqual(0, stubMemory.Deleted.Count, "Ageing out must not hard-delete.");
        CollectionAssert.AreEqual(new[] { "obs_existing_stale" }, stubMemory.Archived.Select(a => a.Id).ToArray(),
            "Aged theory's memory entry should be archived; fresh theory's preserved");
        Assert.AreEqual("observation theory aged out", stubMemory.Archived[0].Reason);
        Assert.IsTrue(stubMemory.Saved.ContainsKey("obs_existing_fresh"));
    }

    [TestMethod]
    public async Task ExecuteAsync_MemorySaveFailure_PhaseStillSucceeds()
    {
        var target = MakeTarget(promotionThreshold: 3);
        await Store().SaveAsync(target, new ObservationState
        {
            Candidates = { Candidate("cand_1", 3, DateTimeOffset.UtcNow) },
        }, CancellationToken.None);

        var stubEval = new StubEvaluator
        {
            Verdicts = { new EvaluationVerdict("cand_1", EvaluationAction.Promote, null, null) },
        };
        var stubMemory = new StubLongTermMemory { ThrowOnSave = true };

        var result = await MakePhase(stubEval, stubMemory).ExecuteAsync(target, CancellationToken.None);

        Assert.AreEqual(1, result.CandidatesPromoted,
            "Phase still reports the promotion even though memory publishing failed");

        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(1, state.Theories.Count,
            "Theory still committed to JSON state — memory side-effects are best-effort");
        Assert.IsNotNull(state.Theories[0].MemoryEntryId,
            "MemoryEntryId is recorded even if the publish failed; reconciliation can recover");
    }

    [TestMethod]
    public async Task ExecuteAsync_MemoryDeleteFailure_PhaseStillSucceeds()
    {
        var target = MakeTarget(theoryAgingDays: 30);
        var now = DateTimeOffset.UtcNow;

        var stale = Theory("stale", now.AddDays(-90));
        stale.MemoryEntryId = "obs_stale";
        await Store().SaveAsync(target, new ObservationState
        {
            Theories = { stale },
        }, CancellationToken.None);

        var stubMemory = new StubLongTermMemory { ThrowOnDelete = true };
        var result = await MakePhase(new StubEvaluator(), stubMemory).ExecuteAsync(target, CancellationToken.None);

        Assert.AreEqual(1, result.TheoriesAged);

        var state = await Store().LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(0, state.Theories.Count,
            "Theory still removed from JSON; orphaned memory entry is recoverable later");
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectedCandidateNotPublishedToMemory()
    {
        var target = MakeTarget(promotionThreshold: 3);
        await Store().SaveAsync(target, new ObservationState
        {
            Candidates = { Candidate("cand_1", 3, DateTimeOffset.UtcNow) },
        }, CancellationToken.None);

        var stubEval = new StubEvaluator
        {
            Verdicts = { new EvaluationVerdict("cand_1", EvaluationAction.Reject, null, null) },
        };
        var stubMemory = new StubLongTermMemory();

        await MakePhase(stubEval, stubMemory).ExecuteAsync(target, CancellationToken.None);

        Assert.AreEqual(0, stubMemory.Saved.Count, "Rejected candidates must not become memory entries");
    }
}

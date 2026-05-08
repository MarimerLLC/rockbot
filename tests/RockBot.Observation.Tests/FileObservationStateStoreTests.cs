using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Observation.Tests;

[TestClass]
public class FileObservationStateStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "rockbot-observation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private ObservationTarget MakeTarget(string name = "test-target") =>
        new()
        {
            Name = name,
            Filter = new PassThroughFilter(),
            ExtractionPrompt = "extract",
            EvaluationPrompt = "evaluate",
            StateFilePath = Path.Combine(_tempDir, $"{name}.json"),
            OutputMarkdownPath = Path.Combine(_tempDir, $"{name}.md"),
        };

    private static IObservationStateStore CreateStore() =>
        new FileObservationStateStore(NullLogger<FileObservationStateStore>.Instance);

    [TestMethod]
    public async Task LoadAsync_NoFile_ReturnsEmptyState()
    {
        var store = CreateStore();
        var state = await store.LoadAsync(MakeTarget(), CancellationToken.None);

        Assert.IsNotNull(state);
        Assert.AreEqual(ObservationState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.AreEqual(0, state.Candidates.Count);
        Assert.AreEqual(0, state.Theories.Count);
        Assert.IsNull(state.LastDreamAt);
    }

    [TestMethod]
    public async Task SaveThenLoad_RoundTrips()
    {
        var store = CreateStore();
        var target = MakeTarget();
        var observed = DateTimeOffset.Parse("2026-05-07T12:00:00Z");

        var state = new ObservationState
        {
            LastDreamAt = observed,
            Candidates =
            {
                new Candidate
                {
                    Id = "cand_a",
                    Text = "Observation A",
                    ClusterId = "c1",
                    Count = 1,
                    FirstSeen = observed,
                    LastSeen = observed,
                    References = { new ObservationReference("conv1", "t1", "quote a", observed) },
                },
            },
        };

        await store.SaveAsync(target, state, CancellationToken.None);

        Assert.IsTrue(File.Exists(target.StateFilePath), "State file should exist after save");

        var rt = await store.LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(1, rt.Candidates.Count);
        Assert.AreEqual("cand_a", rt.Candidates[0].Id);
        Assert.AreEqual(observed, rt.LastDreamAt);
    }

    [TestMethod]
    public async Task SaveAsync_WritesViaTempFile()
    {
        var store = CreateStore();
        var target = MakeTarget();
        var state = new ObservationState();

        await store.SaveAsync(target, state, CancellationToken.None);

        // Atomic-rename pattern: the temp file should NOT exist after a clean save
        Assert.IsFalse(File.Exists(target.StateFilePath + ".tmp"),
            "Temp file should be renamed away on successful save");
        Assert.IsTrue(File.Exists(target.StateFilePath));
    }

    [TestMethod]
    public async Task SaveAsync_OverwritesExistingFile()
    {
        var store = CreateStore();
        var target = MakeTarget();

        await store.SaveAsync(target, new ObservationState
        {
            Candidates = { new Candidate
            {
                Id = "first", Text = "first", ClusterId = "c", Count = 0,
                FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow,
            } },
        }, CancellationToken.None);

        await store.SaveAsync(target, new ObservationState
        {
            Candidates = { new Candidate
            {
                Id = "second", Text = "second", ClusterId = "c", Count = 0,
                FirstSeen = DateTimeOffset.UtcNow, LastSeen = DateTimeOffset.UtcNow,
            } },
        }, CancellationToken.None);

        var rt = await store.LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(1, rt.Candidates.Count);
        Assert.AreEqual("second", rt.Candidates[0].Id);
    }

    [TestMethod]
    public async Task LoadAsync_UnknownSchemaVersion_Throws()
    {
        var target = MakeTarget();
        await File.WriteAllTextAsync(target.StateFilePath,
            "{\"schemaVersion\": 99, \"candidates\": [], \"theories\": [], \"snapshots\": []}");

        var store = CreateStore();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.LoadAsync(target, CancellationToken.None));
    }

    [TestMethod]
    public async Task SaveAsync_AlwaysWritesCurrentSchemaVersion()
    {
        var store = CreateStore();
        var target = MakeTarget();

        // Even if a caller hands in a state with the wrong schemaVersion,
        // the store normalises to the current one on write.
        var state = new ObservationState { SchemaVersion = 0 };
        await store.SaveAsync(target, state, CancellationToken.None);

        var rt = await store.LoadAsync(target, CancellationToken.None);
        Assert.AreEqual(ObservationState.CurrentSchemaVersion, rt.SchemaVersion);
    }

    [TestMethod]
    public async Task SaveAsync_CreatesDirectoryIfMissing()
    {
        var store = CreateStore();
        var nestedDir = Path.Combine(_tempDir, "nested", "deeper");
        var target = new ObservationTarget
        {
            Name = "deep",
            Filter = new PassThroughFilter(),
            ExtractionPrompt = "x",
            EvaluationPrompt = "x",
            StateFilePath = Path.Combine(nestedDir, "deep.json"),
            OutputMarkdownPath = Path.Combine(nestedDir, "deep.md"),
        };

        await store.SaveAsync(target, new ObservationState(), CancellationToken.None);

        Assert.IsTrue(File.Exists(target.StateFilePath));
    }

    private sealed class PassThroughFilter : ITranscriptFilter
    {
        public IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns) => turns;
    }
}

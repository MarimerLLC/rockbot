using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class SchemaMigrationRunnerTests
{
    private const string StoreName = "test-store";

    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-schema-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task RunAsync_NewStore_StampsCurrentVersionAndRunsNoMigrations()
    {
        var migration = new RecordingMigration(1, 2);
        var runner = CreateRunner(currentVersion: 2, migrations: [migration]);

        await runner.RunAsync();

        Assert.IsFalse(migration.Ran, "A directory with no data is a new store, not a legacy one.");
        Assert.AreEqual(2, ReadMarker()!.Version);
    }

    [TestMethod]
    public async Task RunAsync_UnmarkedStoreWithData_AtCurrentVersion_StampsWithoutMigrating()
    {
        WriteData("entry.json");
        var migration = new RecordingMigration(1, 2);
        var runner = CreateRunner(currentVersion: 1, migrations: [migration]);

        await runner.RunAsync();

        Assert.IsFalse(migration.Ran);
        var marker = ReadMarker();
        Assert.AreEqual(1, marker!.Version);
        Assert.AreEqual(StoreName, marker.Store);
    }

    [TestMethod]
    public async Task RunAsync_UnmarkedStoreWithData_BelowCurrentVersion_MigratesFromLegacyVersion()
    {
        WriteData("entry.json");
        var migration = new RecordingMigration(1, 2);
        var runner = CreateRunner(currentVersion: 2, migrations: [migration]);

        await runner.RunAsync();

        Assert.IsTrue(migration.Ran);
        Assert.AreEqual(1, migration.Context!.FromVersion);
        Assert.AreEqual(2, migration.Context.ToVersion);
        Assert.AreEqual(StoreName, migration.Context.StoreName);
        Assert.AreEqual(_tempDir, migration.Context.StorePath);
        Assert.AreEqual(2, ReadMarker()!.Version);
    }

    [TestMethod]
    public async Task RunAsync_MultipleVersionsBehind_RunsMigrationsInOrder()
    {
        await WriteMarkerAsync(StoreName, version: 1);
        var order = new List<int>();
        var first = new RecordingMigration(1, 2, onRun: () => order.Add(1));
        var second = new RecordingMigration(2, 3, onRun: () => order.Add(2));

        // Registered out of order on purpose — the chain walk, not registration order, decides.
        var runner = CreateRunner(currentVersion: 3, migrations: [second, first]);

        await runner.RunAsync();

        CollectionAssert.AreEqual(new[] { 1, 2 }, order);
        Assert.AreEqual(3, ReadMarker()!.Version);
    }

    [TestMethod]
    public async Task RunAsync_MarkerAheadOfBuild_LeavesStoreUntouched()
    {
        await WriteMarkerAsync(StoreName, version: 5);
        var migration = new RecordingMigration(1, 2);
        var runner = CreateRunner(currentVersion: 2, migrations: [migration]);

        await runner.RunAsync();

        Assert.IsFalse(migration.Ran);
        Assert.AreEqual(5, ReadMarker()!.Version, "A rollback must not rewrite a newer build's marker.");
    }

    [TestMethod]
    public async Task RunAsync_GapInMigrationChain_Throws()
    {
        await WriteMarkerAsync(StoreName, version: 1);
        var runner = CreateRunner(currentVersion: 3, migrations: [new RecordingMigration(2, 3)]);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runner.RunAsync());
        StringAssert.Contains(ex.Message, "no migration from v1");
    }

    [TestMethod]
    public async Task RunAsync_TwoMigrationsClaimingSameStep_Throws()
    {
        await WriteMarkerAsync(StoreName, version: 1);
        var runner = CreateRunner(
            currentVersion: 2,
            migrations: [new RecordingMigration(1, 2), new RecordingMigration(1, 2)]);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runner.RunAsync());
        StringAssert.Contains(ex.Message, "Exactly one migration may claim a version step");
    }

    [TestMethod]
    public async Task RunAsync_DryRun_RunsNothingAndWritesNoMarker()
    {
        WriteData("entry.json");
        var migration = new RecordingMigration(1, 2);
        var runner = CreateRunner(currentVersion: 2, migrations: [migration], dryRun: true);

        await runner.RunAsync();

        Assert.IsFalse(migration.Ran);
        Assert.IsFalse(File.Exists(MarkerPath));
    }

    [TestMethod]
    public async Task RunAsync_Disabled_DoesNothing()
    {
        WriteData("entry.json");
        var migration = new RecordingMigration(1, 2);
        var runner = CreateRunner(currentVersion: 2, migrations: [migration], enabled: false);

        await runner.RunAsync();

        Assert.IsFalse(migration.Ran);
        Assert.IsFalse(File.Exists(MarkerPath));
    }

    [TestMethod]
    public async Task RunAsync_MigrationThrows_LeavesMarkerAtLastCompletedStep()
    {
        await WriteMarkerAsync(StoreName, version: 1);
        var runner = CreateRunner(
            currentVersion: 3,
            migrations: [new RecordingMigration(1, 2), new ThrowingMigration(2, 3)]);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runner.RunAsync());

        Assert.AreEqual(2, ReadMarker()!.Version,
            "The step that succeeded should be stamped so a restart resumes rather than replaying it.");
    }

    [TestMethod]
    public async Task RunAsync_MarkerBelongsToAnotherStore_SkipsWithoutMigrating()
    {
        await WriteMarkerAsync("some-other-store", version: 1);
        var migration = new RecordingMigration(1, 2);
        var runner = CreateRunner(currentVersion: 2, migrations: [migration]);

        await runner.RunAsync();

        Assert.IsFalse(migration.Ran);
        Assert.AreEqual("some-other-store", ReadMarker()!.Store);
        Assert.AreEqual(1, ReadMarker()!.Version);
    }

    [TestMethod]
    public async Task RunAsync_UnreadableMarker_TreatsStoreAsUnmarked()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(MarkerPath, "{ not json");
        WriteData("entry.json");
        var migration = new RecordingMigration(1, 2);
        var runner = CreateRunner(currentVersion: 2, migrations: [migration]);

        await runner.RunAsync();

        Assert.IsTrue(migration.Ran, "Corrupt marker plus real data reads as a pre-mechanism store.");
        Assert.AreEqual(2, ReadMarker()!.Version);
    }

    [TestMethod]
    public async Task RunAsync_EveryStoreIsVisited()
    {
        var otherDir = Path.Combine(_tempDir, "second");
        var descriptors = new[]
        {
            new StoreSchemaDescriptor(StoreName, 1, _ => _tempDir),
            new StoreSchemaDescriptor("second-store", 1, _ => otherDir)
        };
        var runner = CreateRunner(descriptors, migrations: []);

        await runner.RunAsync();

        Assert.AreEqual(StoreName, ReadMarker()!.Store);
        Assert.AreEqual("second-store", ReadMarker(otherDir)!.Store);
    }

    [TestMethod]
    public async Task Marker_IsInvisibleToTheMemoryStoreIndexWalk()
    {
        // The marker sits in the store root, which FileMemoryStore indexes by enumerating
        // *.json recursively. A marker matching that pattern would show up as an entry.
        await StoreSchemaMarkerFile.WriteAsync(
            _tempDir, "memory", version: 1, DateTimeOffset.UtcNow);

        var store = CreateMemoryStore();
        await store.SaveAsync(new MemoryEntry(
            "m1", "The marker must not be indexed", null, ["fact"], DateTimeOffset.UtcNow));
        await store.SaveAsync(new MemoryEntry(
            "m2", "Neither entry should be crowded out", null, ["fact"], DateTimeOffset.UtcNow));

        var all = await store.SearchAsync(new MemorySearchCriteria(MaxResults: 50));

        CollectionAssert.AreEquivalent(new[] { "m1", "m2" }, all.Select(e => e.Id).ToArray());
        Assert.IsTrue(File.Exists(MarkerPath), "The store must not have deleted or rewritten the marker.");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private string MarkerPath => Path.Combine(_tempDir, StoreSchemaMarker.FileName);

    private SchemaMigrationRunner CreateRunner(
        int currentVersion,
        IEnumerable<ISchemaMigration> migrations,
        bool dryRun = false,
        bool enabled = true,
        int legacyVersion = 1) =>
        CreateRunner(
            [new StoreSchemaDescriptor(StoreName, currentVersion, _ => _tempDir, legacyVersion)],
            migrations,
            dryRun,
            enabled);

    private static SchemaMigrationRunner CreateRunner(
        IEnumerable<StoreSchemaDescriptor> descriptors,
        IEnumerable<ISchemaMigration> migrations,
        bool dryRun = false,
        bool enabled = true) =>
        new(descriptors,
            migrations,
            Options.Create(new SchemaMigrationOptions { DryRun = dryRun, Enabled = enabled }),
            new EmptyServiceProvider(),
            NullLogger<SchemaMigrationRunner>.Instance);

    private FileMemoryStore CreateMemoryStore() =>
        new(Options.Create(new MemoryOptions { BasePath = _tempDir }),
            Options.Create(new AgentProfileOptions()),
            Options.Create(new EmbeddingOptions()),
            NullLogger<FileMemoryStore>.Instance,
            EmbeddingTextPreparer.ForTests());

    private void WriteData(string fileName)
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, fileName), "{}");
    }

    private Task WriteMarkerAsync(string storeName, int version) =>
        StoreSchemaMarkerFile.WriteAsync(_tempDir, storeName, version, DateTimeOffset.UtcNow);

    private StoreSchemaMarker? ReadMarker(string? dir = null)
    {
        var path = Path.Combine(dir ?? _tempDir, StoreSchemaMarker.FileName);
        if (!File.Exists(path))
            return null;

        return JsonSerializer.Deserialize<StoreSchemaMarker>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private sealed class RecordingMigration(int from, int to, Action? onRun = null) : ISchemaMigration
    {
        public string StoreName => SchemaMigrationRunnerTests.StoreName;
        public int FromVersion => from;
        public int ToVersion => to;

        public bool Ran { get; private set; }
        public SchemaMigrationContext? Context { get; private set; }

        public Task MigrateAsync(SchemaMigrationContext context, CancellationToken cancellationToken = default)
        {
            Ran = true;
            Context = context;
            onRun?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingMigration(int from, int to) : ISchemaMigration
    {
        public string StoreName => SchemaMigrationRunnerTests.StoreName;
        public int FromVersion => from;
        public int ToVersion => to;

        public Task MigrateAsync(SchemaMigrationContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("migration failed");
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

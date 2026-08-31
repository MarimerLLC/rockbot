using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

[TestClass]
public class EmbeddingCacheTests
{
    [TestMethod]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var v = new float[] { 1, 2, 3 };
        var result = EmbeddingCache.CosineSimilarity(v, v);
        Assert.AreEqual(1.0f, result, 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1, 0 };
        var b = new float[] { 0, 1 };
        var result = EmbeddingCache.CosineSimilarity(a, b);
        Assert.AreEqual(0.0f, result, 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        var a = new float[] { 1, 0 };
        var b = new float[] { -1, 0 };
        var result = EmbeddingCache.CosineSimilarity(a, b);
        Assert.AreEqual(-1.0f, result, 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_ZeroVector_ReturnsZero()
    {
        var a = new float[] { 1, 2, 3 };
        var b = new float[] { 0, 0, 0 };
        var result = EmbeddingCache.CosineSimilarity(a, b);
        Assert.AreEqual(0.0f, result, 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_DifferentLengths_ReturnsZero()
    {
        var a = new float[] { 1, 2 };
        var b = new float[] { 1, 2, 3 };
        var result = EmbeddingCache.CosineSimilarity(a, b);
        Assert.AreEqual(0.0f, result, 0.001f);
    }

    [TestMethod]
    public void CosineSimilarity_SimilarVectors_ReturnsHighScore()
    {
        var a = new float[] { 0.9f, 0.1f };
        var b = new float[] { 0.85f, 0.15f };
        var result = EmbeddingCache.CosineSimilarity(a, b);
        Assert.IsTrue(result > 0.99f, $"Expected high similarity, got {result}");
    }

    [TestMethod]
    public void CosineSimilarity_EmptyVectors_ReturnsZero()
    {
        var result = EmbeddingCache.CosineSimilarity([], []);
        Assert.AreEqual(0.0f, result, 0.001f);
    }

    // ── Empty-directory hygiene ──────────────────────────────

    [TestMethod]
    public void Constructor_PrunesPreexistingEmptyDirectories()
    {
        // Older builds deleted a namespaced entry's .bin but left its directory, so the
        // cache mirrored the store's phantom-directory problem one level down.
        using var temp = new TempDir();
        var embeddings = Path.Combine(temp.Path, EmbeddingCache.DirectoryName);
        Directory.CreateDirectory(Path.Combine(embeddings, "todo"));
        Directory.CreateDirectory(Path.Combine(embeddings, "mcp", "calendar"));
        Directory.CreateDirectory(Path.Combine(embeddings, "research"));
        File.WriteAllBytes(Path.Combine(embeddings, "research", "summarize.bin"), [1, 2, 3, 4]);

        _ = CreateCache(temp.Path);

        Assert.IsFalse(Directory.Exists(Path.Combine(embeddings, "todo")));
        Assert.IsFalse(Directory.Exists(Path.Combine(embeddings, "mcp")));
        Assert.IsTrue(Directory.Exists(Path.Combine(embeddings, "research")));
        Assert.IsTrue(Directory.Exists(embeddings), "the cache root must survive the prune");
    }

    [TestMethod]
    public void Remove_LastEntryInNamespace_RemovesEmptyDirectory()
    {
        using var temp = new TempDir();
        var embeddings = Path.Combine(temp.Path, EmbeddingCache.DirectoryName);
        Directory.CreateDirectory(Path.Combine(embeddings, "research"));
        File.WriteAllBytes(Path.Combine(embeddings, "research", "summarize.bin"), [1, 2, 3, 4]);

        var cache = CreateCache(temp.Path);
        cache.Remove("research/summarize");

        Assert.IsFalse(File.Exists(Path.Combine(embeddings, "research", "summarize.bin")));
        Assert.IsFalse(Directory.Exists(Path.Combine(embeddings, "research")));
        Assert.IsTrue(Directory.Exists(embeddings));
    }

    [TestMethod]
    public void Remove_NamespaceWithRemainingEntry_KeepsDirectory()
    {
        using var temp = new TempDir();
        var embeddings = Path.Combine(temp.Path, EmbeddingCache.DirectoryName);
        Directory.CreateDirectory(Path.Combine(embeddings, "research"));
        File.WriteAllBytes(Path.Combine(embeddings, "research", "summarize.bin"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(embeddings, "research", "scan.bin"), [5, 6, 7, 8]);

        var cache = CreateCache(temp.Path);
        cache.Remove("research/summarize");

        Assert.IsTrue(Directory.Exists(Path.Combine(embeddings, "research")));
        Assert.IsTrue(File.Exists(Path.Combine(embeddings, "research", "scan.bin")));
    }

    // ── Helpers ────────────────────────────────────────────

    private static EmbeddingCache CreateCache(string storePath) =>
        new(new StubEmbeddingGenerator(), storePath, NullLogger.Instance, EmbeddingTextPreparer.ForTests());

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "rockbot-embedding-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                list.Add(new Embedding<float>(new float[] { 0f, 0f, 0f }));
            return Task.FromResult(list);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

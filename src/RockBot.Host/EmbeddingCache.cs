using System.Collections.Concurrent;
using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// File-backed cache for text embeddings. Embeddings are stored as raw float arrays
/// in <c>{basePath}/.embeddings/{id}.bin</c>. Generates missing embeddings on demand
/// via the supplied <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>.
/// </summary>
internal sealed class EmbeddingCache
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly string _embeddingsPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Tombstone set: IDs that have been deleted while a background <see cref="UpdateAsync"/>
    /// may still be in flight. Prevents orphaned .bin files from the save-then-delete race.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _deleted = new(StringComparer.OrdinalIgnoreCase);

    public EmbeddingCache(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string storePath,
        ILogger logger)
    {
        _generator = generator;
        _embeddingsPath = Path.Combine(storePath, ".embeddings");
        _logger = logger;

        Directory.CreateDirectory(_embeddingsPath);
    }

    /// <summary>
    /// Returns the cached embedding for <paramref name="id"/>, generating and persisting it
    /// if not yet cached. Returns null if generation fails.
    /// </summary>
    public async Task<float[]?> GetOrCreateAsync(string id, string text, CancellationToken ct = default)
    {
        var filePath = GetFilePath(id);

        // Fast path: read from disk (no lock needed for concurrent reads of immutable files)
        var cached = await TryReadAsync(filePath);
        if (cached is not null)
            return cached;

        await _semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            cached = await TryReadAsync(filePath);
            if (cached is not null)
                return cached;

            var embedding = await GenerateAsync(text, ct);
            if (embedding is null)
                return null;

            await WriteAsync(filePath, embedding);
            return embedding;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Generates and caches an embedding for a document, replacing any existing cached value.
    /// Called on save/update to keep the cache warm. Skips the write if the ID was deleted
    /// while generation was in flight (prevents orphaned .bin files).
    /// </summary>
    public async Task UpdateAsync(string id, string text, CancellationToken ct = default)
    {
        // Clear any previous tombstone — this is a new save for this ID.
        _deleted.TryRemove(id, out _);

        var embedding = await GenerateAsync(text, ct);
        if (embedding is null)
            return;

        // Check tombstone: if Remove was called while we were generating, skip the write.
        if (_deleted.TryRemove(id, out _))
        {
            _logger.LogDebug("Skipping embedding write for '{Id}' — deleted during generation", id);
            return;
        }

        await _semaphore.WaitAsync(ct);
        try
        {
            await WriteAsync(GetFilePath(id), embedding);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Removes the cached embedding for the given ID and sets a tombstone
    /// so any in-flight <see cref="UpdateAsync"/> will not re-create it.
    /// </summary>
    public void Remove(string id)
    {
        _deleted[id] = 0;

        var filePath = GetFilePath(id);
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    /// <summary>
    /// Generates an embedding for a query string (not cached).
    /// </summary>
    public async Task<float[]?> GenerateQueryEmbeddingAsync(string query, CancellationToken ct = default)
        => await GenerateAsync(query, ct);

    /// <summary>
    /// Computes cosine similarity between two vectors.
    /// Returns 0 if either vector is zero-length or null.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0f;

        var dot = TensorPrimitives.Dot(a.AsSpan(), b.AsSpan());
        var magA = MathF.Sqrt(TensorPrimitives.Dot(a.AsSpan(), a.AsSpan()));
        var magB = MathF.Sqrt(TensorPrimitives.Dot(b.AsSpan(), b.AsSpan()));

        if (magA == 0 || magB == 0)
            return 0f;

        return dot / (magA * magB);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<float[]?> GenerateAsync(string text, CancellationToken ct)
    {
        try
        {
            var result = await _generator.GenerateAsync(text, cancellationToken: ct);
            return result.Vector.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate embedding — falling back to BM25-only");
            return null;
        }
    }

    private static async Task<float[]?> TryReadAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(filePath);
            var floats = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteAsync(string filePath, float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        await File.WriteAllBytesAsync(filePath, bytes);
    }

    private string GetFilePath(string id) =>
        Path.Combine(_embeddingsPath, id + ".bin");
}

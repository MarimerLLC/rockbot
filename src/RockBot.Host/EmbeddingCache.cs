using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly EmbeddingTextPreparer _preparer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Tombstone set: IDs that have been deleted while a background <see cref="UpdateAsync"/>
    /// may still be in flight. Prevents orphaned .bin files from the save-then-delete race.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _deleted = new(StringComparer.OrdinalIgnoreCase);

    public EmbeddingCache(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string storePath,
        ILogger logger,
        EmbeddingTextPreparer preparer)
    {
        _generator = generator;
        _embeddingsPath = Path.Combine(storePath, ".embeddings");
        _logger = logger;
        _preparer = preparer;

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
    /// Returns cached embeddings for the given IDs, generating and caching any that are missing
    /// in a single batched call to the embedding endpoint. Much faster than sequential
    /// <see cref="GetOrCreateAsync"/> calls when multiple candidates have cache misses.
    /// </summary>
    public async Task<Dictionary<string, float[]?>> GetOrCreateBatchAsync(
        IReadOnlyList<(string Id, string Text)> items,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, float[]?>(items.Count, StringComparer.OrdinalIgnoreCase);
        var misses = new List<(string Id, string Text)>();

        // Fast path: read all cached embeddings without locking
        foreach (var (id, text) in items)
        {
            var cached = await TryReadAsync(GetFilePath(id));
            if (cached is not null)
                result[id] = cached;
            else
                misses.Add((id, text));
        }

        if (misses.Count == 0)
            return result;

        await _semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock — another thread may have generated some
            var stillMissing = new List<(string Id, string Text)>();
            foreach (var (id, text) in misses)
            {
                var cached = await TryReadAsync(GetFilePath(id));
                if (cached is not null)
                    result[id] = cached;
                else
                    stillMissing.Add((id, text));
            }

            if (stillMissing.Count == 0)
                return result;

            // Batch-generate all missing embeddings in one call
            var sw = Stopwatch.StartNew();
            try
            {
                HostDiagnostics.EmbeddingCalls.Add(1);

                var texts = stillMissing
                    .Select(m => _preparer.Prepare(m.Text, diagnosticKey: m.Id))
                    .ToList();
                var generated = await _generator.GenerateAsync(texts, cancellationToken: ct);

                sw.Stop();
                HostDiagnostics.EmbeddingDuration.Record(sw.Elapsed.TotalMilliseconds);
                _logger.LogInformation(
                    "Batch embedding generated {Count} vectors in {Duration:F0}ms ({Dimensions} dimensions)",
                    stillMissing.Count, sw.Elapsed.TotalMilliseconds,
                    generated.Count > 0 ? generated[0].Vector.Length : 0);

                for (var i = 0; i < stillMissing.Count && i < generated.Count; i++)
                {
                    var embedding = generated[i].Vector.ToArray();
                    result[stillMissing[i].Id] = embedding;
                    await WriteAsync(GetFilePath(stillMissing[i].Id), embedding);
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                HostDiagnostics.EmbeddingFailures.Add(1);
                HostDiagnostics.EmbeddingDuration.Record(sw.Elapsed.TotalMilliseconds);
                _logger.LogWarning(ex,
                    "Batch embedding generation failed for {Count} items in {Duration:F0}ms — returning nulls",
                    stillMissing.Count, sw.Elapsed.TotalMilliseconds);

                foreach (var (id, _) in stillMissing)
                    result.TryAdd(id, null);
            }
        }
        finally
        {
            _semaphore.Release();
        }

        return result;
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
        using var activity = HostDiagnostics.Source.StartActivity("rockbot.embedding.generate");
        var sw = Stopwatch.StartNew();
        try
        {
            HostDiagnostics.EmbeddingCalls.Add(1);

            text = _preparer.Prepare(text);

            var result = await _generator.GenerateAsync(text, cancellationToken: ct);
            sw.Stop();
            HostDiagnostics.EmbeddingDuration.Record(sw.Elapsed.TotalMilliseconds);
            _logger.LogInformation("Embedding generated in {Duration:F0}ms ({Dimensions} dimensions)",
                sw.Elapsed.TotalMilliseconds, result.Vector.Length);
            return result.Vector.ToArray();
        }
        catch (Exception ex)
        {
            sw.Stop();
            HostDiagnostics.EmbeddingFailures.Add(1);
            HostDiagnostics.EmbeddingDuration.Record(sw.Elapsed.TotalMilliseconds);
            _logger.LogWarning(ex, "Failed to generate embedding in {Duration:F0}ms — falling back to BM25-only",
                sw.Elapsed.TotalMilliseconds);
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
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        await File.WriteAllBytesAsync(filePath, bytes);
    }

    private string GetFilePath(string id) =>
        Path.Combine(_embeddingsPath, id + ".bin");
}

namespace RockBot.Host;

/// <summary>
/// Stores chunked content (a page, a large tool result, wisp input) in working memory as
/// <c>{keyBase}-chunk{n}</c> entries plus a <c>{keyBase}-index</c> outline, and reports what
/// actually got stored (#801). A full namespace makes the store evict older regenerable content;
/// if it still can't take a chunk, storing stops there and the result says how much is missing,
/// so callers never tell the model that something is in working memory when it isn't.
/// </summary>
public static class ChunkedContentStore
{
    /// <summary>What <see cref="StoreAsync"/> stored.</summary>
    /// <param name="Chunks">All chunks of the content.</param>
    /// <param name="StoredKeys">Keys of the chunks stored, in order: chunk <c>i</c> is <c>StoredKeys[i]</c>.</param>
    /// <param name="IndexKey">The outline's key, or null when it couldn't be stored.</param>
    /// <param name="Reason">Why storing stopped short, or null when everything was stored.</param>
    public sealed record Result(
        IReadOnlyList<ChunkInfo> Chunks,
        IReadOnlyList<string> StoredKeys,
        string? IndexKey,
        string? Reason)
    {
        /// <summary>Chunks that couldn't be stored.</summary>
        public int NotStored => Chunks.Count - StoredKeys.Count;

        /// <summary>True when every chunk and the outline were stored.</summary>
        public bool IsComplete => NotStored == 0 && (IndexKey is not null || Chunks.Count == 0);

        /// <summary>
        /// A line for the model when storing stopped short, or null. It says what is missing and
        /// that the content shown in the table is all there is.
        /// </summary>
        public string? ShortfallNote => IsComplete
            ? null
            : $"WARNING: only {StoredKeys.Count} of {Chunks.Count} chunk(s) could be stored" +
              (IndexKey is null ? ", and no outline" : "") +
              $" ({Reason}). The chunks listed below are all that is available; the rest of the content " +
              "is NOT in working memory. Work from what is listed, or free space (delete entries you no " +
              "longer need) and fetch the content again.";
    }

    /// <summary>
    /// Stores <paramref name="chunks"/> under <paramref name="keyBase"/>, then an outline of the
    /// chunks that were stored. Stops at the first chunk the store refuses.
    /// </summary>
    public static async Task<Result> StoreAsync(
        IWorkingMemory workingMemory,
        string keyBase,
        IReadOnlyList<ChunkInfo> chunks,
        TimeSpan ttl,
        string category)
    {
        var storedKeys = new List<string>(chunks.Count);
        string? reason = null;

        for (var i = 0; i < chunks.Count; i++)
        {
            var key = $"{keyBase}-chunk{i}";
            var write = await workingMemory.TrySetAsync(key, chunks[i].Content, ttl, category);
            if (!write.Stored)
            {
                reason = write.Reason ?? "working memory refused the write";
                break;
            }
            storedKeys.Add(key);
        }

        string? indexKey = null;
        if (storedKeys.Count > 0)
        {
            var key = $"{keyBase}-index";
            var storedChunks = chunks.Take(storedKeys.Count).ToList();
            var write = await workingMemory.TrySetAsync(key, ContentChunker.BuildOutline(storedChunks, storedKeys), ttl, category);
            if (write.Stored)
                indexKey = key;
            else
                reason ??= write.Reason ?? "working memory refused the write";
        }

        return new Result(chunks, storedKeys, indexKey, reason);
    }
}

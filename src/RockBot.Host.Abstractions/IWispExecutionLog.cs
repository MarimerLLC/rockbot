namespace RockBot.Host;

/// <summary>
/// Persistent log of wisp pipeline executions.
/// Entries are written after every wisp run (success or failure) and queried
/// by the dream system to detect recurring failure patterns and identify
/// skill improvement candidates.
/// </summary>
public interface IWispExecutionLog
{
    /// <summary>Appends a wisp execution record to the log.</summary>
    Task AppendAsync(WispExecutionRecord record, CancellationToken ct = default);

    /// <summary>
    /// Returns wisp execution records on or after <paramref name="since"/>,
    /// ordered by timestamp ascending, capped at <paramref name="maxResults"/>.
    /// </summary>
    Task<IReadOnlyList<WispExecutionRecord>> QueryRecentAsync(
        DateTimeOffset since, int maxResults, CancellationToken ct = default);

    /// <summary>
    /// Returns every wisp execution record on or after <paramref name="since"/> whose
    /// <see cref="WispExecutionRecord.ShapeHash"/> or <see cref="WispExecutionRecord.DefinitionHash"/>
    /// equals <paramref name="hash"/> (ordinal, case-insensitive), ordered by timestamp ascending.
    /// Not capped, so other wisps' runs in the window cannot push matching records out of the
    /// result the way a capped <see cref="QueryRecentAsync"/> can.
    /// </summary>
    /// <remarks>
    /// The default implementation filters an uncapped <see cref="QueryRecentAsync"/>;
    /// implementations with an index should override it.
    /// </remarks>
    async Task<IReadOnlyList<WispExecutionRecord>> QueryByHashAsync(
        string hash, DateTimeOffset since, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(hash))
            return [];

        var records = await QueryRecentAsync(since, int.MaxValue, ct);
        return records
            .Where(r => string.Equals(r.ShapeHash, hash, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(r.DefinitionHash, hash, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Returns recent failed records matching the given definition hash,
    /// for detecting retries. Most recent first.
    /// </summary>
    Task<WispExecutionRecord?> FindRecentFailureAsync(
        string definitionHash, string? sessionId, CancellationToken ct = default);

    /// <summary>
    /// Returns the canonical JSON step-definition body for a successful wisp run
    /// matching <paramref name="definitionHash"/>, if one is on file. Used by the
    /// success-shaped dream pass to recover the exact body of a repeating pattern
    /// for promotion to a skill resource.
    /// Returns <c>null</c> when no successful record with this hash carries a body
    /// (failed runs, oversize runs, pre-field records).
    /// </summary>
    Task<string?> GetCanonicalBodyAsync(string definitionHash, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}

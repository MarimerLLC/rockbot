namespace RockBot.Host;

/// <summary>
/// Hot-path contradiction detector for Phase 3 self-repair. Resolves conflicting beliefs
/// at memory-write time, narrowly scoped to capability claims (<c>claim/capability/*</c>)
/// and feedback memories (<c>feedback/*</c>). Saves outside those categories return
/// <see cref="ContradictionResolution.None"/> without scanning.
/// </summary>
/// <remarks>
/// Detection is keyword-based and deterministic; the LLM-mediated dream contradiction
/// sweep is the backstop for cases this hot path misses. User-tagged corrections always
/// win over agent-self entries regardless of recency (see <see cref="FeedbackMemoryCategories"/>).
/// </remarks>
public interface IMemoryContradictionDetector
{
    /// <summary>
    /// Scans existing entries in the same narrow category as <paramref name="incoming"/> and
    /// returns a <see cref="ContradictionResolution"/> describing which entries (if any)
    /// should be marked as superseded.
    /// </summary>
    Task<ContradictionResolution> ResolveAsync(MemoryEntry incoming, CancellationToken cancellationToken = default);
}

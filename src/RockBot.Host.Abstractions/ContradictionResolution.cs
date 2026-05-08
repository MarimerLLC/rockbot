namespace RockBot.Host;

/// <summary>
/// Outcome of running the Phase 3 contradiction detector against an incoming
/// <see cref="MemoryEntry"/>. Encodes which side of the contradiction wins:
/// the incoming entry (in which case <see cref="ExistingIdsToSupersede"/>
/// names the older entries to mark with <see cref="MemoryEntry.SupersededBy"/>),
/// or an existing user-correction entry (in which case
/// <see cref="IncomingSupersededBy"/> is set so the caller saves the incoming
/// entry already marked as superseded).
/// </summary>
/// <remarks>
/// Exactly one of <see cref="ExistingIdsToSupersede"/> or
/// <see cref="IncomingSupersededBy"/> carries content. Use
/// <see cref="None"/> for "no contradiction detected".
/// </remarks>
public sealed record ContradictionResolution
{
    /// <summary>"No contradiction detected" sentinel.</summary>
    public static ContradictionResolution None { get; } = new();

    private ContradictionResolution() { }

    /// <summary>
    /// Older entries that the incoming entry contradicts and replaces.
    /// Caller marks each one's <see cref="MemoryEntry.SupersededBy"/> with the incoming entry id.
    /// </summary>
    public IReadOnlyList<string> ExistingIdsToSupersede { get; init; } = [];

    /// <summary>
    /// Id of an existing user-correction entry that contradicts and supersedes the incoming
    /// entry. When set, the caller persists the incoming entry with
    /// <see cref="MemoryEntry.SupersededBy"/> equal to this id.
    /// </summary>
    public string? IncomingSupersededBy { get; init; }

    /// <summary>The incoming entry wins; caller marks the listed older entries as superseded.</summary>
    public static ContradictionResolution NewerWins(IReadOnlyList<string> existingIds) =>
        new() { ExistingIdsToSupersede = existingIds };

    /// <summary>An existing user-correction wins; caller marks the incoming entry as superseded.</summary>
    public static ContradictionResolution UserCorrectionWins(string existingId) =>
        new() { IncomingSupersededBy = existingId };

    /// <summary>True when the resolution would change any state.</summary>
    public bool HasContradiction =>
        ExistingIdsToSupersede.Count > 0 || IncomingSupersededBy is not null;
}

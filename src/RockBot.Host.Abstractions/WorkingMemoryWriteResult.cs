namespace RockBot.Host;

/// <summary>
/// The outcome of <see cref="IWorkingMemory.TrySetAsync"/> (#801).
/// </summary>
/// <param name="Stored">True when the entry was written.</param>
/// <param name="Reason">Why it wasn't, when <paramref name="Stored"/> is false.</param>
/// <param name="EvictedKeys">Entries removed to make room for it, if any.</param>
public sealed record WorkingMemoryWriteResult(
    bool Stored,
    string? Reason = null,
    IReadOnlyList<string>? EvictedKeys = null)
{
    /// <summary>Stored, with nothing evicted.</summary>
    public static WorkingMemoryWriteResult Ok { get; } = new(true);

    /// <summary>Refused for <paramref name="reason"/>.</summary>
    public static WorkingMemoryWriteResult Refused(string reason) => new(false, reason);
}

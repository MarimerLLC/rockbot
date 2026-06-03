namespace RockBot.Host;

/// <summary>
/// Retention knobs applied by the dream cycle's log-retention pass.
/// Single-file append-only logs honour <see cref="MaxLinesPerFile"/>;
/// per-session directory logs (one <c>{id}.jsonl</c> per session) honour
/// <see cref="MaxFileAge"/> and <see cref="MaxFilesPerDirectory"/>.
/// A non-positive value disables the corresponding dimension.
/// </summary>
public sealed record LogRetentionPolicy(
    TimeSpan MaxFileAge,
    int MaxFilesPerDirectory,
    int MaxLinesPerFile);

/// <summary>
/// Opt-in contract for append-only logs that can prune themselves. The file-backed
/// JSONL stores implement this; <see cref="DreamService"/> resolves every registered
/// instance and invokes <see cref="PruneAsync"/> once per dream cycle so the logs
/// don't grow without bound. Implementations know their own on-disk layout and
/// delegate the actual file work to <see cref="JsonlLogRetention"/>.
/// </summary>
public interface IPrunableLog
{
    /// <summary>
    /// Applies <paramref name="policy"/> to this log and returns the number of files
    /// or lines removed. Best-effort: implementations log and swallow I/O failures
    /// rather than throwing, so one failing log never aborts the retention sweep.
    /// </summary>
    Task<int> PruneAsync(LogRetentionPolicy policy, CancellationToken ct = default);
}

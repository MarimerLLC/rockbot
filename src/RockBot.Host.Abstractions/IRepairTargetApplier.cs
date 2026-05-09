using System.Text.Json;

namespace RockBot.Host;

/// <summary>
/// Applies a <see cref="RepairTicket"/>'s <c>Change</c> payload against its
/// <see cref="RepairTicket.Target"/>. One implementation per <see cref="RepairTarget"/>
/// value; the dream-service apply pass picks the matching applier from the
/// registered enumerable. See <c>design/self-repair.md</c> Phase 4.
/// </summary>
public interface IRepairTargetApplier
{
    /// <summary>Which target this applier handles. The apply pass dispatches on this value.</summary>
    RepairTarget Target { get; }

    /// <summary>
    /// Applies the change. Throws on malformed payloads — the dream-service apply
    /// pass catches and records the failure as an Uncertain attempt rather than
    /// propagating the exception.
    /// </summary>
    Task<RepairApplyOutcome> ApplyAsync(RepairTicket ticket, CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of <see cref="IRepairTargetApplier.ApplyAsync"/>.
/// </summary>
/// <param name="AppliedDiff">
/// Structured description of what the applier did. Recorded on the
/// <see cref="RepairAttempt"/> so subsequent cycles can dedup by change-hash.
/// </param>
/// <param name="Revert">
/// Optional callback that undoes the change. Only set when the applier supports
/// reversal (currently only <c>SkillBody</c>); the dream-service apply pass invokes
/// it when the post-apply verify fails so a bad change cannot cascade.
/// </param>
public sealed record RepairApplyOutcome(
    JsonElement AppliedDiff,
    Func<CancellationToken, Task>? Revert);

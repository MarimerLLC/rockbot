using System.Text.Json;

namespace RockBot.Host;

/// <summary>
/// A self-repair work item: "apply <see cref="Change"/> to <see cref="Target"/>,
/// then run <see cref="Verify"/> — if verify succeeds, mark resolved; otherwise
/// retry up to <c>RepairTicketOptions.MaxAttempts</c> times before escalating."
/// Persisted to PVC as one JSON file per ticket so updates are atomic via temp+rename.
/// See <c>design/self-repair.md</c> Phase 4.
/// </summary>
/// <param name="Id">Stable ticket identifier (used as filename and dedup key).</param>
/// <param name="PatternKey">
/// Canonical string form of the originating <see cref="ClusterKey"/> — <c>"server|tool|errorClass"</c>.
/// Used to dedup ticket creation against the same failure cluster.
/// </param>
/// <param name="Target">Which apply contract this ticket invokes.</param>
/// <param name="Change">JSON payload interpreted by the matching <see cref="IRepairTargetApplier"/>.</param>
/// <param name="Verify">Predicate that decides whether the change resolved the cluster.</param>
/// <param name="Attempts">Append-only attempt history. Empty for newly-created tickets.</param>
/// <param name="Status">Lifecycle state.</param>
/// <param name="CreatedAt">When the ticket was first opened.</param>
/// <param name="UpdatedAt">When the ticket was last persisted (apply, verify, status change).</param>
public sealed record RepairTicket(
    string Id,
    string PatternKey,
    RepairTarget Target,
    JsonElement Change,
    VerifyShape Verify,
    IReadOnlyList<RepairAttempt> Attempts,
    RepairStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

namespace RockBot.Host;

/// <summary>Why a run's findings were, or were not, pushed as a message.</summary>
internal enum MemoryAuditAlertReason
{
    /// <summary>Nothing to push: healthy, alerting off, or an unchanged warning not yet due a repeat.</summary>
    None,

    /// <summary>An alert-severity invariant failed. These are pushed on every run.</summary>
    Alert,

    /// <summary>The set of failing invariants differs from the last one pushed.</summary>
    Changed,

    /// <summary>The same warning as last time, repeated after <see cref="MemoryAuditOptions.AlertRepeatInterval"/>.</summary>
    Repeat
}

/// <summary>
/// The outcome of <see cref="MemoryAuditAlertPolicy.Decide"/>.
/// </summary>
/// <param name="Send">Whether an alert message should be pushed.</param>
/// <param name="Reason">Why.</param>
/// <param name="Current">Failing invariant names on this run, ordinal-sorted.</param>
/// <param name="Added">Names failing now that the last pushed message did not report.</param>
/// <param name="Cleared">Names the last pushed message reported that no longer fail.</param>
internal sealed record MemoryAuditAlertDecision(
    bool Send,
    MemoryAuditAlertReason Reason,
    IReadOnlyList<string> Current,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Cleared);

/// <summary>
/// Decides whether a run's findings are news.
/// </summary>
/// <remarks>
/// A channel that reports "all clear" daily stops being read before the day it matters, and one
/// that reports the same warning daily wears out the same way. So a warning is pushed when the
/// set of failing invariants changes, and repeated unchanged only on a slow cadence. Alert-severity
/// findings — something was actually lost — are pushed every time regardless.
/// </remarks>
internal static class MemoryAuditAlertPolicy
{
    /// <param name="snapshot">This run's snapshot, invariants and status set.</param>
    /// <param name="lastAlertedInvariants">Names the last pushed message reported; empty if none.</param>
    /// <param name="lastAlertedAt">When that message was pushed, or null if never.</param>
    /// <param name="options">Alerting switch and repeat interval.</param>
    /// <param name="now">Agent-local time of this run.</param>
    internal static MemoryAuditAlertDecision Decide(
        MemoryAuditSnapshot snapshot,
        IReadOnlyList<string> lastAlertedInvariants,
        DateTimeOffset? lastAlertedAt,
        MemoryAuditOptions options,
        DateTimeOffset now)
    {
        List<string> current = [.. snapshot.Invariants
            .Select(v => v.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        var previous = new HashSet<string>(lastAlertedInvariants, StringComparer.Ordinal);
        List<string> added = [.. current.Where(name => !previous.Contains(name))];
        List<string> cleared = [.. previous.Where(name => !current.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)];

        MemoryAuditAlertDecision Result(MemoryAuditAlertReason reason) =>
            new(reason != MemoryAuditAlertReason.None, reason, current, added, cleared);

        if (!options.AlertOnAttention || current.Count == 0)
            return Result(MemoryAuditAlertReason.None);

        if (current.Any(MemoryAuditInvariants.IsAlertSeverity))
            return Result(MemoryAuditAlertReason.Alert);

        if (added.Count > 0 || cleared.Count > 0)
            return Result(MemoryAuditAlertReason.Changed);

        var repeatDue = options.AlertRepeatInterval > TimeSpan.Zero
            && (lastAlertedAt is null || now - lastAlertedAt.Value >= options.AlertRepeatInterval);

        return Result(repeatDue ? MemoryAuditAlertReason.Repeat : MemoryAuditAlertReason.None);
    }
}

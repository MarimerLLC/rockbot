using System.Globalization;

namespace RockBot.Host;

/// <summary>
/// Renders when and by whom a working-memory entry was written, and marks snapshot entries
/// (cached copies of state that lives in another system — todos, calendar, mail) so they
/// cannot be mistaken for live tool output. Issue #668: a heartbeat-patrol snapshot of the
/// todo list reached the model with no "as of" signal and beat the live todo tool.
/// </summary>
public static class WorkingMemoryProvenance
{
    /// <summary>Wording used wherever a stored-at timestamp is not known.</summary>
    public const string UnknownStoredAt = "stored at unknown";

    /// <summary>Formats an instant as compact UTC (e.g. <c>2026-10-07T05:04Z</c>).</summary>
    public static string FormatUtc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Humanizes an age as "just now", "5 minutes ago", "1 hour ago", "2 days ago".
    /// Negative ages (clock skew) read as "just now".
    /// </summary>
    public static string HumanizeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return Plural((int)age.TotalMinutes, "minute");
        if (age < TimeSpan.FromDays(1)) return Plural((int)age.TotalHours, "hour");
        return Plural((int)age.TotalDays, "day");

        static string Plural(int n, string unit) => n == 1 ? $"1 {unit} ago" : $"{n} {unit}s ago";
    }

    /// <summary>
    /// "stored 2026-10-07T05:04Z (2 days ago) by patrol/heartbeat-patrol", omitting the
    /// writer when unknown, or <see cref="UnknownStoredAt"/> when there is no timestamp.
    /// </summary>
    public static string Describe(WorkingMemoryEntry? entry, DateTimeOffset now)
    {
        var writer = string.IsNullOrWhiteSpace(entry?.Writer) ? "" : $" by {entry!.Writer}";
        if (entry is null || !entry.HasStoredAt)
            return UnknownStoredAt + writer;
        return $"stored {FormatUtc(entry.StoredAt)} ({HumanizeAge(now - entry.StoredAt)}){writer}";
    }

    /// <summary>True when <paramref name="key"/> falls under one of the configured snapshot prefixes.</summary>
    public static bool IsSnapshotKey(string key, WorkingMemoryOptions options) =>
        options.EffectiveSnapshotKeyPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a snapshot is older than <see cref="WorkingMemoryOptions.SnapshotStaleAfter"/>.
    /// An entry with no known stored-at counts as stale — its freshness cannot be shown.
    /// </summary>
    public static bool IsStale(WorkingMemoryEntry? entry, DateTimeOffset now, WorkingMemoryOptions options) =>
        entry is null || !entry.HasStoredAt || now - entry.StoredAt > options.SnapshotStaleAfter;

    /// <summary>
    /// The header line placed above a value returned by <c>get_from_working_memory</c>.
    /// Snapshot keys get the "may be stale — call the live tool" banner (and a prominent
    /// STALE flag past the stale-after window); every other key gets a one-line provenance note.
    /// </summary>
    public static string BuildHeader(string key, WorkingMemoryEntry? entry, DateTimeOffset now, WorkingMemoryOptions options)
    {
        if (!IsSnapshotKey(key, options))
            return $"[{Describe(entry, now)}]";

        var writer = string.IsNullOrWhiteSpace(entry?.Writer) ? "an unknown writer" : entry!.Writer;
        var when = entry is { HasStoredAt: true }
            ? $"at {FormatUtc(entry.StoredAt)} ({HumanizeAge(now - entry.StoredAt)})"
            : "at an unknown time";
        const string liveHint =
            "For current state (todos, calendar, mail) call the live tool and prefer it if they disagree.";

        if (IsStale(entry, now, options))
            return $"[STALE SNAPSHOT — written by {writer} {when}, older than the " +
                   $"{FormatWindow(options.SnapshotStaleAfter)} stale_after window. Do not present it as current state. " +
                   liveHint + "]";

        return $"[snapshot written by {writer} {when} — may be stale. {liveHint}]";
    }

    /// <summary>
    /// Short marker appended to a search/list line for a snapshot key, or empty for other keys.
    /// </summary>
    public static string SnapshotMarker(WorkingMemoryEntry entry, DateTimeOffset now, WorkingMemoryOptions options)
    {
        if (!IsSnapshotKey(entry.Key, options)) return "";
        return IsStale(entry, now, options)
            ? " [STALE SNAPSHOT — prefer live tools for current state]"
            : " [snapshot — may be stale; prefer live tools for current state]";
    }

    private static string FormatWindow(TimeSpan window)
    {
        if (window.TotalDays >= 1 && window.TotalDays == Math.Floor(window.TotalDays))
            return $"{(int)window.TotalDays}d";
        if (window.TotalHours >= 1 && window.TotalHours == Math.Floor(window.TotalHours))
            return $"{(int)window.TotalHours}h";
        return $"{(int)Math.Max(1, window.TotalMinutes)}m";
    }
}

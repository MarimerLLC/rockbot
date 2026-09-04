using System.Globalization;
using System.Text;

namespace RockBot.Host;

/// <summary>
/// Renders a snapshot as markdown a person can read without knowing the schema.
/// </summary>
/// <remarks>
/// The report is the deliverable, not the JSON. Every previous memory investigation ended with
/// someone writing this document by hand from log greps; the audit's value is that the document
/// already exists when the question is asked. It is deliberately short — status, what moved,
/// what needs attention, and enough trend to see a slope.
/// </remarks>
internal static class MemoryAuditReportWriter
{
    /// <summary>Trend rows shown in the table.</summary>
    private const int TrendRows = 30;

    /// <summary>
    /// Renders <paramref name="snapshot"/>, using <paramref name="trend"/> (oldest first,
    /// including <paramref name="snapshot"/> itself) for the history table.
    /// </summary>
    internal static string Render(
        MemoryAuditSnapshot snapshot,
        IReadOnlyList<MemoryAuditSnapshot> trend,
        MemoryAuditEvalResult? eval = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Memory audit");
        sb.AppendLine();
        sb.AppendLine(
            $"**{StatusLabel(snapshot.Status)}** — {snapshot.Live} live entries, {snapshot.Archived} archived, " +
            $"measured {Timestamp(snapshot.TakenAt)} (`{snapshot.SnapshotId}`).");
        sb.AppendLine();

        // ── What changed ──────────────────────────────────────────────────────
        sb.AppendLine("## What changed");
        sb.AppendLine();

        if (snapshot.PreviousTakenAt is null)
        {
            sb.AppendLine(
                "First run — there is nothing to compare against yet. The numbers below are the " +
                "baseline every later run is measured from.");
        }
        else
        {
            sb.AppendLine($"Since {Timestamp(snapshot.PreviousTakenAt!.Value)}:");
            sb.AppendLine();
            sb.AppendLine($"- {snapshot.CreatedSinceLast} new, {snapshot.ArchivedSinceLast} archived, " +
                          $"{snapshot.HardDeletedSinceLast} gone from disk " +
                          $"({snapshot.PurgedSinceLast} explained by the retention purge).");
            sb.AppendLine(snapshot.NetGrowthPerDay is { } rate
                ? $"- Net growth {FormatRate(rate)} entries/day."
                : "- Net growth: not measurable — the gap between these two runs was too short " +
                  "to read a daily rate from (usually a restart).");
            sb.AppendLine($"- {snapshot.ReinforcedWithoutMergeSinceLast} entries genuinely re-observed " +
                          "(reinforced without a merge).");
            sb.AppendLine($"- {snapshot.DreamPassesRunSinceLast} dream pass(es) ran; " +
                          $"{snapshot.RestartsSinceLast} process restart(s).");
        }

        sb.AppendLine();
        sb.AppendLine("Current shape:");
        sb.AppendLine();
        sb.AppendLine($"- Near-duplicates: {snapshot.NearDupPairs} pair(s) across " +
                      $"{snapshot.NearDupEntries} entries" +
                      (snapshot.EmbeddingDupClusters is { } clusters
                          ? $"; the store's own detector finds {clusters} cluster(s)."
                          : "."));
        sb.AppendLine($"- Merge chains: deepest is {snapshot.MaxChainDepth}" +
                      (snapshot.MergeChainDepth.Count > 0
                          ? $" ({FormatHistogram(snapshot.MergeChainDepth)})."
                          : "."));
        sb.AppendLine($"- Reinforcement: {FormatHistogram(snapshot.Reinforcement)}.");
        sb.AppendLine($"- Purge outlook: {snapshot.Purge.Count} archived entry(s) are hard-deleted within " +
                      $"{snapshot.Purge.DueWithinDays} days; {snapshot.Purge.HighValueCount} of those are held " +
                      "back by the high-value floor.");

        if (snapshot.TopCategoriesByGrowth.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Categories that moved most:");
            sb.AppendLine();
            foreach (var category in snapshot.TopCategoriesByGrowth)
                sb.AppendLine($"- `{category.Category}` — {Signed(category.Net)} " +
                              $"({category.Created} new, {category.Archived} archived)");
        }

        // ── Needs attention ───────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("## Needs attention");
        sb.AppendLine();

        if (snapshot.Invariants.Count == 0)
        {
            sb.AppendLine("Nothing. Every invariant held and no threshold was crossed.");
        }
        else
        {
            foreach (var violation in snapshot.Invariants)
            {
                sb.AppendLine($"- **`{violation.Name}`** — {violation.Message}");
                if (violation.Ids.Count > 0)
                    sb.AppendLine($"  - ids: {string.Join(", ", violation.Ids.Select(id => $"`{id}`"))}");
            }
        }

        // ── Trend ─────────────────────────────────────────────────────────────
        var rows = trend.Count <= TrendRows ? trend : [.. trend.Skip(trend.Count - TrendRows)];
        if (rows.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine($"## Trend (last {rows.Count} runs)");
            sb.AppendLine();
            sb.AppendLine("| Date | Live | Archived | New | Retired | Deleted | Net/day | Status |");
            sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (var row in rows)
                sb.AppendLine(
                    $"| {Date(row.TakenAt)} | {row.Live} | {row.Archived} | {row.CreatedSinceLast} | " +
                    $"{row.ArchivedSinceLast} | {row.HardDeletedSinceLast} | " +
                    $"{FormatRate(row.NetGrowthPerDay)} | {row.Status} |");
        }

        // ── Eval ──────────────────────────────────────────────────────────────
        var summary = eval?.Summary ?? snapshot.Eval;
        if (summary is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Sample eval");
            sb.AppendLine();
            sb.AppendLine(
                $"On {Date(summary.EvaluatedAt)} a judge reviewed {summary.Sampled} sampled outcome(s) and " +
                $"agreed with {summary.Sound} of them ({Percent(summary.SoundRate)}).");

            if (summary.RateByCategory.Count > 0)
            {
                sb.AppendLine();
                foreach (var (category, rate) in summary.RateByCategory.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    sb.AppendLine($"- {category}: {Percent(rate)} sound");
            }

            var unsound = eval?.Verdicts.Where(v => !v.Sound).Take(5).ToList() ?? [];
            if (unsound.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("The judge disagreed with:");
                sb.AppendLine();
                foreach (var verdict in unsound)
                    sb.AppendLine($"- [{verdict.Category}] {string.Join(", ", verdict.Ids.Select(id => $"`{id}`"))} — " +
                                  $"{verdict.Reason ?? "no reason given"}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// A percentage built by hand rather than with the "P0" specifier.
    /// </summary>
    /// <remarks>
    /// Under the invariant culture the agent container actually runs with, ICU renders "P0" as
    /// <c>75 %</c> — with a space — while a developer machine on en-US renders <c>75%</c>. The
    /// report is a generated document; its bytes must not depend on the host's locale.
    /// </remarks>
    private static string Percent(double fraction) =>
        (fraction * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A signed count, with the ASCII sign rather than a locale's own.</summary>
    private static string Signed(int value) =>
        value.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    /// <summary>A date on the Gregorian calendar regardless of the host's default.</summary>
    private static string Date(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A date and time on the Gregorian calendar regardless of the host's default.</summary>
    private static string Timestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>An unmeasurable rate reads as a dash, never as zero.</summary>
    private static string FormatRate(double? rate) =>
        rate is { } value ? value.ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture) : "—";

    private static string StatusLabel(string status) => status switch
    {
        MemoryAuditStatuses.Alert => "ALERT",
        MemoryAuditStatuses.Warning => "Warning",
        _ => "Healthy"
    };

    private static string FormatHistogram(IReadOnlyDictionary<string, int> histogram) =>
        histogram.Count == 0
            ? "none"
            : string.Join(", ", histogram.Select(kv => $"{kv.Key}: {kv.Value}"));
}

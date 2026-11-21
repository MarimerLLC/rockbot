using System.Globalization;

namespace RockBot.Host;

/// <summary>
/// Picks the recurring tasks worth an efficiency review (#848): ones that cost real money per run
/// and show a sign the money buys little. Every other self-improvement pass is triggered by a
/// failure or a frequency, so a task that succeeds every week at a high cost trips none of them.
/// Deterministic, no LLM call; the dream's efficiency pass sends only these tasks to the model.
/// </summary>
public static class ScheduledTaskEfficiencyAdvisor
{
    /// <summary>The thresholds the selection uses.</summary>
    /// <param name="MinRuns">Runs needed since the task was created or last optimized.</param>
    /// <param name="MinMedianCostUsd">Median cost per run below which a task is left alone.</param>
    /// <param name="MinToolCalls">Median tool calls per run that by itself marks a task as heavy.</param>
    public sealed record Thresholds(int MinRuns = 3, double MinMedianCostUsd = 0.10, int MinToolCalls = 15);

    /// <summary>A task picked for review, with the runs it was judged on and why.</summary>
    /// <param name="Runs">The runs since the task's last optimization, oldest first.</param>
    /// <param name="MedianCostUsd">Their median cost.</param>
    /// <param name="Signals">Why the task was picked, one short line each.</param>
    public sealed record Candidate(
        ScheduledTask Task,
        IReadOnlyList<ScheduledRunRecord> Runs,
        double MedianCostUsd,
        IReadOnlyList<string> Signals);

    /// <summary>
    /// <paramref name="task"/> as a candidate, or null to leave it alone. A task qualifies when it
    /// is a user task that recurs, has at least <see cref="Thresholds.MinRuns"/> runs since
    /// <paramref name="lastOptimization"/> (so a change is judged on runs made after it), costs at
    /// least <see cref="Thresholds.MinMedianCostUsd"/> a run, and shows at least one signal:
    /// <list type="bullet">
    ///   <item>at least <see cref="Thresholds.MinRuns"/> runs reported the same thing, or nothing;</item>
    ///   <item>a run repeated a fetch it had already made;</item>
    ///   <item>its runs make at least <see cref="Thresholds.MinToolCalls"/> tool calls (median).</item>
    /// </list>
    /// </summary>
    public static Candidate? Select(
        ScheduledTask task,
        IReadOnlyList<ScheduledRunRecord> runs,
        ScheduledTaskOptimization? lastOptimization,
        Thresholds thresholds)
    {
        if (task.IsSystemTask || task.RunOnce)
            return null;

        var since = lastOptimization is null
            ? runs.ToList()
            : runs.Where(r => r.StartedAt >= lastOptimization.At).ToList();
        var minRuns = Math.Max(1, thresholds.MinRuns);
        if (since.Count < minRuns)
            return null;

        var median = ScheduledTaskRunCosts.Median(since.Select(r => r.CostUsd)) ?? 0;
        if (median < thresholds.MinMedianCostUsd)
            return null;

        var signals = new List<string>();

        // The same report (digits and whitespace folded) or no report, run after run. A gated
        // skip is the gate doing its job, not a wasted run.
        var ran = since.Where(r => r.Outcome != ScheduledRunOutcome.GatedSkip).ToList();
        var empty = ran.Count(r => r.Outcome == ScheduledRunOutcome.Empty);
        var sameReport = ran
            .Where(r => r.ReplyHash is not null)
            .GroupBy(r => r.ReplyHash)
            .Select(g => g.Count())
            .DefaultIfEmpty(0)
            .Max();
        if (empty + sameReport >= minRuns)
            signals.Add(empty >= sameReport
                ? $"{empty + sameReport} of {ran.Count} runs reported nothing or the same thing"
                : $"{sameReport} of {ran.Count} runs sent the same report" + (empty > 0 ? $" and {empty} sent none" : ""));

        var duplicates = since.Sum(r => r.DuplicateFetches ?? 0);
        if (duplicates > 0)
            signals.Add($"{duplicates} repeated fetch(es) across {since.Count(r => r.DuplicateFetches > 0)} run(s)");

        var calls = since.Where(r => r.ToolCalls is not null).Select(r => (double)r.ToolCalls!.Value).ToList();
        if (ScheduledTaskRunCosts.Median(calls) is { } medianCalls && medianCalls >= thresholds.MinToolCalls)
            signals.Add(string.Create(CultureInfo.InvariantCulture, $"a median {medianCalls:0.#} tool calls per run"));

        return signals.Count == 0 ? null : new Candidate(task, since, median, signals);
    }
}

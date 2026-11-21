using System.Globalization;

namespace RockBot.Host;

/// <summary>
/// Decides from a scheduled task's run history whether to cap it at Balanced (#846). Recurring work
/// has a known cost profile after a few runs; routing re-decides from text the patrol LLM writes
/// fresh each time, so a task that Balanced handles week after week can still land on High and cost
/// several times as much. Deterministic, no LLM call: the dream runs it on every cycle.
/// </summary>
/// <remarks>
/// It only ever adds a Balanced ceiling to a task that has none. It never lowers a task to Low
/// (cheaper, but the run history says nothing about whether Low would do the job) and never lifts
/// a ceiling; rescheduling the task (a new definition) starts it over without one.
/// </remarks>
public static class ScheduledTaskTierAdvisor
{
    /// <summary>The ceiling to set on <paramref name="task"/> and why, or null to leave it alone.</summary>
    public static (ModelTier MaxTier, string Reason)? Advise(
        ScheduledTask task, IReadOnlyList<ScheduledRunRecord> runs, ScheduledTaskCostOptions options, DateTimeOffset now)
    {
        if (task.MaxTier is not null || task.RunOnce)
            return null;

        // A gated skip (#848) ran no LLM turn, so it says nothing about which tier does the job.
        var recent = runs.Where(r => r.Outcome != ScheduledRunOutcome.GatedSkip)
            .TakeLast(options.BaselineRuns).Where(r => r.HighestTier is not null).ToList();
        var high = recent.Where(r => r.HighestTier == ModelTier.High).Select(r => r.CostUsd).ToList();
        var balanced = recent.Where(r => r.HighestTier is ModelTier.Low or ModelTier.Balanced).Select(r => r.CostUsd).ToList();
        if (high.Count == 0 || balanced.Count < options.TierCeilingMinBalancedRuns)
            return null;

        var highMedian = ScheduledTaskRunCosts.Median(high)!.Value;
        var balancedMedian = ScheduledTaskRunCosts.Median(balanced)!.Value;
        if (balancedMedian <= 0 || highMedian < options.TierCeilingCostRatio * balancedMedian)
            return null;

        var reason = string.Create(CultureInfo.InvariantCulture,
            $"{high.Count} of its last {recent.Count} runs used High at a median ${highMedian:0.00}, " +
            $"{highMedian / balancedMedian:0.#}× the ${balancedMedian:0.00} median of {balanced.Count} runs " +
            $"that finished on Balanced or lower (dream, {now:yyyy-MM-dd})");
        return (ModelTier.Balanced, reason);
    }
}

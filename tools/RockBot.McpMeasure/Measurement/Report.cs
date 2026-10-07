using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RockBot.McpMeasure.Measurement;

/// <summary>Writes the raw rows (results.jsonl) and the tables (summary.md).</summary>
public static class Report
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task AppendAsync(string path, IEnumerable<TurnResult> rows, CancellationToken ct)
    {
        var lines = rows.Select(r => JsonSerializer.Serialize(r, Json));
        await File.AppendAllLinesAsync(path, lines, ct);
    }

    public static IReadOnlyList<TurnResult> Read(string path) =>
        [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<TurnResult>(l, Json)!)];

    public static string Summarize(IReadOnlyList<TurnResult> all, string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header).AppendLine();

        // A turn that threw (a provider error, usually a rate limit) says nothing about the
        // model's calls; it is left out of every rate and counted at the end.
        var rows = all.Where(r => r.Error is null).ToList();

        var conditions = rows
            .GroupBy(r => (r.Model, r.Mode))
            .OrderBy(g => ModelOrder(g.Key.Model)).ThenBy(g => ModeOrder(g.Key.Mode))
            .ToList();

        sb.AppendLine("## Per model and mode").AppendLine();
        sb.AppendLine("| Model | Mode | Turns | Target call right first time | First call correct | First call well-formed | Completed | Tool calls | Downstream attempts | Input tokens | Cached | Output tokens | Wall s |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var g in conditions)
        {
            var n = g.Count();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {g.Key.Model} ({g.First().ModelId}) | {g.Key.Mode} | {n} | {Pct(g, TargetRightFirstTime)} | {Pct(g, r => r.FirstCallCorrect)} | {Pct(g, r => r.FirstCallWellFormed)} | " +
                $"{Pct(g, r => r.Completed)} | {g.Average(r => r.ToolCalls):F1} | {g.Average(r => r.DownstreamAttempts):F1} | " +
                $"{g.Average(r => r.InputTokens):F0} | {g.Average(r => r.CachedInputTokens):F0} | {g.Average(r => r.OutputTokens):F0} | " +
                $"{g.Average(r => r.WallMs) / 1000:F1} |");
        }

        var tasks = rows.Select(r => r.Task).Distinct().ToList();
        sb.AppendLine().AppendLine("## Target call right first time, by task").AppendLine();
        sb.AppendLine("| Model | Mode | " + string.Join(" | ", tasks) + " |");
        sb.AppendLine("|---|---|" + string.Concat(tasks.Select(_ => "---:|")));
        foreach (var g in conditions)
        {
            var cells = tasks.Select(t =>
            {
                var turns = g.Where(r => r.Task == t).ToList();
                return turns.Count == 0 ? "–" : $"{turns.Count(TargetRightFirstTime)}/{turns.Count}";
            });
            sb.AppendLine($"| {g.Key.Model} | {g.Key.Mode} | {string.Join(" | ", cells)} |");
        }

        sb.AppendLine().AppendLine("## First call correct, by task").AppendLine();
        sb.AppendLine("| Model | Mode | " + string.Join(" | ", tasks) + " |");
        sb.AppendLine("|---|---|" + string.Concat(tasks.Select(_ => "---:|")));
        foreach (var g in conditions)
        {
            var cells = tasks.Select(t =>
            {
                var turns = g.Where(r => r.Task == t).ToList();
                return turns.Count == 0 ? "–" : $"{turns.Count(r => r.FirstCallCorrect)}/{turns.Count}";
            });
            sb.AppendLine($"| {g.Key.Model} | {g.Key.Mode} | {string.Join(" | ", cells)} |");
        }

        sb.AppendLine().AppendLine("## Why the first call wasn't correct").AppendLine();
        var failures = rows.Where(r => r.Failure is not null).Select(r => r.Failure!).Distinct().Order().ToList();
        if (failures.Count == 0)
        {
            sb.AppendLine("Every first call was correct.");
        }
        else
        {
            sb.AppendLine("| Model | Mode | " + string.Join(" | ", failures) + " |");
            sb.AppendLine("|---|---|" + string.Concat(failures.Select(_ => "---:|")));
            foreach (var g in conditions)
            {
                var cells = failures.Select(f => g.Count(r => r.Failure == f) is var c and > 0 ? c.ToString(CultureInfo.InvariantCulture) : "");
                sb.AppendLine($"| {g.Key.Model} | {g.Key.Mode} | {string.Join(" | ", cells)} |");
            }
        }

        var errors = all.Count - rows.Count;
        if (errors > 0)
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture,
                $"{errors} turn(s) ended in an error and are left out of the tables above; see results.jsonl.");

        return sb.ToString();
    }

    /// <summary>
    /// The first call to the turn's target tool — whatever preparatory calls came before it, such
    /// as <c>list_accounts</c> or a fresh search — was valid and did what was asked. This isolates
    /// how well the surface lets the model write the arguments, which is what #420 was about.
    /// </summary>
    public static bool TargetRightFirstTime(TurnResult row)
    {
        var turn = TurnFor(row.Task);
        var first = row.Attempts.FirstOrDefault(a =>
            a.Tool == turn.Tool && string.Equals(a.Server, turn.Server, StringComparison.OrdinalIgnoreCase));
        return first is { Outcome: Outcome.Ok }
               && turn.Check(JsonDocument.Parse(first.ArgsJson).RootElement) is null;
    }

    private static MeasureTurn TurnFor(string taskId)
    {
        var parts = taskId.Split('#');
        var task = MeasureTasks.All.Single(t => t.Id == parts[0]);
        return task.Turns[parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) - 1 : 0];
    }

    private static string Pct(IEnumerable<TurnResult> rows, Func<TurnResult, bool> pick)
    {
        var list = rows.ToList();
        return list.Count == 0 ? "–" : $"{100.0 * list.Count(pick) / list.Count:F0}%";
    }

    private static int ModeOrder(string mode) => mode switch
    {
        "Off" => 0, "Eager" => 1, "Lazy" => 2, "Pinned" => 3, _ => 4
    };

    private static int ModelOrder(string model) => model switch
    {
        "high" => 0, "balanced" => 1, "low" => 2, _ => 3
    };
}

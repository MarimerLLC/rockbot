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

    public static string Summarize(IReadOnlyList<TurnResult> rows, string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header).AppendLine();

        var conditions = rows
            .GroupBy(r => (r.Model, r.Mode))
            .OrderBy(g => ModelOrder(g.Key.Model)).ThenBy(g => ModeOrder(g.Key.Mode))
            .ToList();

        sb.AppendLine("## Per model and mode").AppendLine();
        sb.AppendLine("| Model | Mode | Turns | First call correct | First call well-formed | Completed | Tool calls | Downstream attempts | Input tokens | Cached | Output tokens | Wall s |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var g in conditions)
        {
            var n = g.Count();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {g.Key.Model} ({g.First().ModelId}) | {g.Key.Mode} | {n} | {Pct(g, r => r.FirstCallCorrect)} | {Pct(g, r => r.FirstCallWellFormed)} | " +
                $"{Pct(g, r => r.Completed)} | {g.Average(r => r.ToolCalls):F1} | {g.Average(r => r.DownstreamAttempts):F1} | " +
                $"{g.Average(r => r.InputTokens):F0} | {g.Average(r => r.CachedInputTokens):F0} | {g.Average(r => r.OutputTokens):F0} | " +
                $"{g.Average(r => r.WallMs) / 1000:F1} |");
        }

        sb.AppendLine().AppendLine("## First call correct, by task").AppendLine();
        var tasks = rows.Select(r => r.Task).Distinct().ToList();
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

        var errors = rows.Count(r => r.Error is not null);
        if (errors > 0)
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"{errors} turn(s) ended in an error; see results.jsonl.");

        return sb.ToString();
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

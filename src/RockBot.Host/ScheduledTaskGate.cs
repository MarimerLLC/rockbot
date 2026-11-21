using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>What a scheduled task's gate decided for one run (#848).</summary>
/// <param name="RunLlm">True when the LLM turn should run.</param>
/// <param name="Result">What the gate did, for the run record.</param>
/// <param name="LastResultHash">The hash to store as the gate's last result.</param>
/// <param name="ConsecutiveSkips">The skip count to store.</param>
/// <param name="Reason">A short explanation for the log.</param>
public sealed record ScheduledTaskGateDecision(
    bool RunLlm,
    ScheduledRunGateResult Result,
    string? LastResultHash,
    int ConsecutiveSkips,
    string Reason);

/// <summary>
/// The deterministic part of a scheduled task's gate (#848): filling the wisp's date placeholders,
/// deciding from its output whether there is anything new, and the skip limit. Pure, so the handler
/// that runs the wisp stays thin.
/// </summary>
public static partial class ScheduledTaskGate
{
    /// <summary>Placeholder for the previous fire as ISO 8601 (UTC).</summary>
    public const string LastRunAtPlaceholder = "{{last_run_at}}";

    /// <summary>Placeholder for the previous fire's date as <c>yyyy/MM/dd</c> (UTC), the form mail search filters take.</summary>
    public const string LastRunDatePlaceholder = "{{last_run_date}}";

    /// <summary>How far back a task that has never fired looks.</summary>
    public static readonly TimeSpan FirstRunLookback = TimeSpan.FromDays(30);

    /// <summary>Keys whose array, when empty, means a result set with nothing in it.</summary>
    private static readonly string[] ResultArrayKeys =
        ["results", "items", "messages", "emails", "events", "value", "data", "matches", "entries", "records", "rows"];

    /// <summary>Keys whose zero means a result set with nothing in it.</summary>
    private static readonly string[] CountKeys =
        ["count", "total", "totalcount", "total_count", "resultcount", "result_count", "resultsizeestimate"];

    /// <summary>
    /// <paramref name="body"/> with the date placeholders filled from <paramref name="lastFiredAt"/>,
    /// or from <see cref="FirstRunLookback"/> before <paramref name="now"/> when the task has not fired.
    /// The values are JSON-safe, so the body stays valid JSON.
    /// </summary>
    public static string FillPlaceholders(string body, DateTimeOffset? lastFiredAt, DateTimeOffset now)
    {
        if (!body.Contains("{{", StringComparison.Ordinal))
            return body;
        var since = (lastFiredAt ?? now - FirstRunLookback).ToUniversalTime();
        return body
            .Replace(LastRunAtPlaceholder, since.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace(LastRunDatePlaceholder, since.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decides whether the LLM turn runs, given the wisp's <paramref name="output"/> and the gate's
    /// stored state. A gate that has skipped <see cref="TaskGate.MaxConsecutiveSkips"/> runs in a
    /// row runs anyway, so a wrong gate cannot silence a task for good.
    /// </summary>
    public static ScheduledTaskGateDecision Evaluate(TaskGate gate, string? output)
    {
        var empty = IsEmpty(output);
        var hash = empty ? gate.LastResultHash : OutputHash(output!);

        string? quiet = null;
        if (empty)
            quiet = "the gate wisp found nothing";
        else if (gate.Mode == TaskGateMode.Changed && gate.LastResultHash is not null
                 && string.Equals(hash, gate.LastResultHash, StringComparison.Ordinal))
            quiet = "the gate wisp found the same results as last run";

        if (quiet is null)
            return new ScheduledTaskGateDecision(true, ScheduledRunGateResult.Passed, hash, 0,
                "the gate wisp found new results");

        var skips = gate.ConsecutiveSkips + 1;
        if (gate.MaxConsecutiveSkips > 0 && skips > gate.MaxConsecutiveSkips)
            return new ScheduledTaskGateDecision(true, ScheduledRunGateResult.ForcedRun, hash, 0,
                $"{quiet}, but the task has skipped {gate.ConsecutiveSkips} runs in a row, so it runs anyway");

        return new ScheduledTaskGateDecision(false, ScheduledRunGateResult.Skipped, hash, skips, quiet);
    }

    /// <summary>
    /// True when <paramref name="output"/> holds no results: blank; JSON <c>null</c>, <c>[]</c> or
    /// <c>{}</c>; an object whose result arrays are all empty or whose count is zero; an MCP text
    /// envelope around any of those; or a short "no results" sentence.
    /// </summary>
    public static bool IsEmpty(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return true;

        var trimmed = output.Trim();
        if (trimmed[0] is '{' or '[' || trimmed == "null")
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return IsEmpty(doc.RootElement);
            }
            catch (JsonException)
            {
                // Not JSON after all; judge it as text.
            }
        }

        return trimmed.Length <= 200 && NoResultsSentence().IsMatch(trimmed);
    }

    private static bool IsEmpty(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;
            case JsonValueKind.String:
                return IsEmpty(element.GetString());
            case JsonValueKind.Array:
                return element.GetArrayLength() == 0;
            case JsonValueKind.Object:
                break;
            default:
                return false;
        }

        var props = element.EnumerateObject().ToList();
        if (props.Count == 0)
            return true;

        // An MCP tool result: {"content":[{"type":"text","text":"..."}]}.
        if (props.FirstOrDefault(p => p.NameEquals("content")) is { Value.ValueKind: JsonValueKind.Array } content
            && content.Value.GetArrayLength() > 0
            && content.Value.EnumerateArray().All(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("text", out _)))
        {
            return content.Value.EnumerateArray().All(i => IsEmpty(i.GetProperty("text").GetString()));
        }

        foreach (var p in props)
        {
            if (CountKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var n))
                return n == 0;
        }

        var arrays = props
            .Where(p => ResultArrayKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                        && p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Null)
            .ToList();
        return arrays.Count > 0 && arrays.All(p => p.Value.ValueKind == JsonValueKind.Null || p.Value.GetArrayLength() == 0);
    }

    /// <summary>A short hash of <paramref name="output"/> with whitespace folded.</summary>
    public static string OutputHash(string output)
    {
        var folded = WhitespaceRun().Replace(output.Trim(), " ");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folded)))[..16];
    }

    // The whole text must be the "nothing" statement: a short tail ("for that query", "since
    // Monday.") may follow, but not a clause ("No new mail, but one invite arrived").
    [GeneratedRegex(@"^\W*(no|zero|0)\s+(new\s+)?(results?|messages?|emails?|mail|items?|matches|events?|records?|entries|rows|hits)(\s+(were\s+)?(found|returned|matched))?\b[^,;]{0,60}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoResultsSentence();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}

using System.Text;
using System.Text.RegularExpressions;
using RockBot.Host;

namespace RockBot.Subagent;

/// <summary>
/// Builds the "Prior work in this conversation" block a new subagent starts with (#665): the user
/// request behind the task, the inputs the primary named, every earlier subagent result in the
/// conversation and the files it wrote — and the full text of the most relevant of them, up to a
/// budget. It is framework-side, so a later subagent sees earlier research even when the primary
/// forgets to pass it along.
/// </summary>
internal static partial class SubagentLineage
{
    internal const string Heading = "## Prior work in this conversation";

    internal const string GroundingInstruction =
        "Ground your work in these prior results; fetch the full items with get_from_working_memory / " +
        "file_read when needed; if a prior result contradicts an older draft, the research wins.";

    /// <summary>Below this many characters of budget left, nothing more is inlined.</summary>
    private const int MinUsefulInlineChars = 200;

    private const int MaxRequestChars = 1_500;

    /// <summary>One piece of inlined text and where the rest of it can be fetched.</summary>
    private sealed record InlinePiece(string Header, string Content, string? FetchHint);

    /// <summary>
    /// The block, or null when there is nothing to say (no request, inputs, results or artifacts).
    /// </summary>
    /// <param name="snapshot">The conversation's work so far.</param>
    /// <param name="description">The new subagent's task description.</param>
    /// <param name="context">The spawn's optional context; joins the description for relevance.</param>
    /// <param name="originatingUserRequest">The user message that led to the spawn (#666).</param>
    /// <param name="inputs">Resolved <c>spawn_subagent</c> inputs; inlined first.</param>
    /// <param name="budgetChars">Characters of full text to inline in total.</param>
    /// <param name="loadKey">Reads a working-memory key; null when missing.</param>
    /// <param name="now">The current time, for "completed N min ago".</param>
    public static async Task<string?> BuildAsync(
        SessionWorkSnapshot snapshot,
        string description,
        string? context,
        string? originatingUserRequest,
        IReadOnlyList<SubagentInput> inputs,
        int budgetChars,
        Func<string, Task<string?>> loadKey,
        DateTimeOffset now)
    {
        var request = string.IsNullOrWhiteSpace(originatingUserRequest) ? null : originatingUserRequest.Trim();
        if (snapshot.IsEmpty && inputs.Count == 0)
        {
            return request is null
                ? null
                : "## The user's request\n" +
                  $"The user message that led to this task: \"{SessionWorkRegistry.Truncate(request, MaxRequestChars)}\"\n" +
                  "Check your result against it before you finish.";
        }

        // ── Decide what is inlined, inputs first ────────────────────────────
        var remaining = Math.Max(0, budgetChars);
        var pieces = new List<InlinePiece>();
        var inlined = new HashSet<string>(StringComparer.Ordinal);
        var truncated = new HashSet<string>(StringComparer.Ordinal);

        bool TryInline(string reference, string header, string content, string fetchHint)
        {
            if (string.IsNullOrWhiteSpace(content) || remaining < MinUsefulInlineChars) return false;
            var cost = header.Length + content.Length + 8;
            if (cost <= remaining)
            {
                pieces.Add(new InlinePiece(header, content, null));
                remaining -= cost;
                inlined.Add(reference);
                return true;
            }

            var room = remaining - header.Length - fetchHint.Length - 40;
            if (room < MinUsefulInlineChars) { remaining = 0; return false; }
            pieces.Add(new InlinePiece(header, content[..room].TrimEnd(), fetchHint));
            remaining = 0;
            inlined.Add(reference);
            truncated.Add(reference);
            return true;
        }

        foreach (var input in inputs)
        {
            var hint = input.Kind == SubagentInputKind.File
                ? $"file_read('{input.Reference}')"
                : $"get_from_working_memory('{input.Reference}')";
            TryInline(input.Reference, $"#### Input: {input.Reference}", input.Content, hint);
        }

        var query = Tokenize(description + " " + context);
        var ranked = RankForInlining(snapshot.Results, query);
        foreach (var result in ranked)
        {
            if (remaining < MinUsefulInlineChars) break;
            TryInline(ResultRef(result), $"#### Result of task {result.TaskId}: {SessionWorkContext.OneLine(result.Description, 160)}",
                result.Output, "the full result is in the primary agent's conversation");
            foreach (var key in result.Keys)
            {
                if (remaining < MinUsefulInlineChars) break;
                if (inlined.Contains(key)) continue;
                var value = await loadKey(key);
                if (value is null) continue;
                TryInline(key, $"#### {key}", value, $"get_from_working_memory('{key}')");
            }
        }

        // ── Render ──────────────────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine(Heading);
        sb.AppendLine(GroundingInstruction);

        if (request is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"The user message that led to this task: \"{SessionWorkRegistry.Truncate(request, MaxRequestChars)}\"");
        }

        if (inputs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Inputs you must use");
            foreach (var input in inputs)
            {
                var what = input.Kind == SubagentInputKind.File ? "file" : "working-memory key";
                sb.AppendLine($"- {what} `{input.Reference}` {Marker(input.Reference, inlined, truncated, input.Kind == SubagentInputKind.File ? $"file_read('{input.Reference}')" : $"get_from_working_memory('{input.Reference}')")}");
            }
        }

        if (snapshot.Results.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Earlier subagent results (newest first)");
            foreach (var r in snapshot.Results)
            {
                var status = r.IsSuccess ? "completed" : "FAILED";
                sb.AppendLine($"- task {r.TaskId} ({status} {Ago(now - r.CompletedAt)}): {SessionWorkContext.OneLine(r.Description, 200)}");
                if (r.Summary.Length > 0)
                    sb.AppendLine($"  Summary: {SessionWorkContext.OneLine(r.Summary, SessionWorkRegistry.MaxSummaryChars)}");
                if (r.Keys.Count > 0 || r.ChunkKeyCount > 0)
                {
                    var keys = string.Join(", ", r.Keys.Select(k =>
                        $"`{k}`" + (inlined.Contains(k) ? (truncated.Contains(k) ? " (partly inlined below)" : " (inlined below)") : string.Empty)));
                    if (r.ChunkKeyCount > 0)
                        keys += (keys.Length > 0 ? ", plus " : string.Empty) + $"{r.ChunkKeyCount} web/tool chunk key(s) under subagent/{r.TaskId}/";
                    sb.AppendLine($"  Saved keys: {keys}");
                }
                if (inlined.Contains(ResultRef(r)))
                    sb.AppendLine($"  Full result text {(truncated.Contains(ResultRef(r)) ? "partly " : string.Empty)}inlined below.");
            }
        }

        if (snapshot.Artifacts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Files and uploads in this conversation (newest first)");
            foreach (var a in snapshot.Artifacts)
                sb.AppendLine($"- {SessionWorkContext.DescribeArtifact(a, snapshot.SessionId)}");
        }

        if (pieces.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Full text of the most relevant prior work");
            foreach (var piece in pieces)
            {
                sb.AppendLine(piece.Header);
                sb.AppendLine(piece.Content);
                if (piece.FetchHint is not null)
                    sb.AppendLine($"…[truncated to fit — {piece.FetchHint}]");
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The results worth inlining, most relevant first: keyword overlap between the new task and
    /// each result's description and summary, most recent first on a tie. Results with no overlap
    /// are left out — except that when none overlaps, the most recent successful result is kept,
    /// since a pipeline's next step usually builds on the step just before it. Failed results are
    /// listed but never inlined.
    /// </summary>
    internal static IReadOnlyList<SubagentWorkResult> RankForInlining(
        IReadOnlyList<SubagentWorkResult> results, IReadOnlySet<string> query)
    {
        var successful = results.Where(r => r.IsSuccess).ToList();
        var scored = successful
            .Select(r => (Result: r, Score: Tokenize(r.Description + " " + r.Summary).Count(query.Contains)))
            .ToList();
        var relevant = scored
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.Result.CompletedAt)
            .Select(s => s.Result)
            .ToList();
        if (relevant.Count > 0) return relevant;
        return successful.OrderByDescending(r => r.CompletedAt).Take(1).ToList();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "about", "your", "you", "are",
        "was", "were", "will", "would", "should", "can", "could", "use", "using", "make", "create",
        "write", "task", "subagent", "please", "each", "all", "any", "its", "their", "them", "then",
        "than", "also", "have", "has", "had", "not", "but", "our", "out", "what", "when", "where",
        "which", "who", "how", "why", "include", "including", "based", "report", "results", "result",
        "list", "save", "saved", "working", "memory", "key", "keys", "find", "give", "return", "user",
        "new", "one", "two", "via", "per", "like", "just", "only", "more", "most", "some", "such",
        "these", "those", "there", "here", "other", "over", "under", "sure", "need", "needs", "want",
    };

    /// <summary>Lower-case words of three or more letters, minus stop words.</summary>
    internal static IReadOnlySet<string> Tokenize(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return set;
        foreach (Match m in WordRegex().Matches(text.ToLowerInvariant()))
        {
            var w = m.Value;
            if (w.Length >= 3 && !StopWords.Contains(w)) set.Add(w);
        }
        return set;
    }

    private static string ResultRef(SubagentWorkResult r) => $"result:{r.TaskId}";

    private static string Marker(string reference, HashSet<string> inlined, HashSet<string> truncated, string fetch) =>
        !inlined.Contains(reference) ? $"(not inlined — fetch with {fetch})"
        : truncated.Contains(reference) ? $"(partly inlined below — fetch the rest with {fetch})"
        : "(inlined in full below)";

    private static string Ago(TimeSpan elapsed) =>
        elapsed.TotalMinutes < 1 ? "just now"
        : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} min ago"
        : $"{elapsed.TotalHours:0.#} h ago";

    [GeneratedRegex(@"[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}

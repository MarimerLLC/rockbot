using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Subagent.Worker;

/// <summary>
/// Tool executor for <c>spawn_workers</c>. Accepts an array of worker definitions,
/// runs them concurrently via <see cref="IWorkerManager"/>, and returns a batch
/// receipt — each worker's findings inlined, followed by one
/// <see cref="WorkerResult"/> per definition.
/// </summary>
/// <remarks>
/// The receipt used to carry <see cref="WorkerResult"/> metadata only, telling the
/// spawning agent to fetch each worker's findings from working memory itself. In
/// practice it often didn't, and answered the user with "I can't report the result"
/// even though the worker had saved a perfectly good one (issue #493). Findings are
/// now inlined up to <c>WorkerOptions.MaxInlineResultChars</c>, so the common case
/// costs no extra round-trip; the key-based fetch survives only for oversized payloads.
/// </remarks>
internal sealed class SpawnWorkersExecutor(
    IWorkerManager manager,
    IWorkingMemory? workingMemory = null,
    int maxInlineChars = 4000,
    ILogger? logger = null) : IToolExecutor
{
    private static readonly JsonSerializerOptions JsonInput = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions JsonOutput = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static readonly IReadOnlyDictionary<string, string?> NoFindings =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Dictionary<string, JsonElement> args;
        try
        {
            args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments) ?? [];
        }
        catch
        {
            return Error(request, "Invalid arguments JSON");
        }

        var definitions = ParseDefinitions(args, out var parseError);
        if (definitions is null)
            return Error(request, parseError!);

        if (definitions.Count == 0)
            return Error(request, "definitions array must contain at least one worker definition");

        var primarySessionId = request.SessionId ?? "unknown";

        var batch = await manager.SpawnBatchAsync(definitions, primarySessionId, ct);

        var findings = await FetchFindingsAsync(batch);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = FormatBatchReceipt(batch, findings, maxInlineChars),
            IsError = false,
        };
    }

    /// <summary>
    /// Reads each distinct <see cref="WorkerResult.ResultKey"/> out of working memory.
    /// A key is present in the returned map only when the read actually succeeded — an
    /// absent key means "could not retrieve", which the receipt reports differently from
    /// "the worker wrote nothing". A failing store degrades the receipt to the
    /// metadata-only shape rather than failing the tool call.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string?>> FetchFindingsAsync(WorkerBatchResult batch)
    {
        if (workingMemory is null || batch.Results.Count == 0)
            return NoFindings;

        var findings = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var result in batch.Results)
        {
            if (string.IsNullOrWhiteSpace(result.ResultKey) || findings.ContainsKey(result.ResultKey))
                continue;

            try
            {
                findings[result.ResultKey] = await workingMemory.GetAsync(result.ResultKey);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex,
                    "spawn_workers: could not read worker findings at '{ResultKey}'; the receipt will ask the agent to fetch it",
                    result.ResultKey);
            }
        }

        return findings;
    }

    private static IReadOnlyList<WorkerDefinition>? ParseDefinitions(
        Dictionary<string, JsonElement> args, out string? error)
    {
        if (!args.TryGetValue("definitions", out var defsEl))
        {
            error = "Missing required argument: definitions";
            return null;
        }

        try
        {
            if (defsEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<WorkerDefinition>();
                foreach (var item in defsEl.EnumerateArray())
                {
                    var json = item.ValueKind == JsonValueKind.String
                        ? item.GetString()!
                        : item.GetRawText();
                    var def = JsonSerializer.Deserialize<WorkerDefinition>(json, JsonInput);
                    if (def is null)
                    {
                        error = "A worker definition deserialised to null";
                        return null;
                    }
                    if (string.IsNullOrWhiteSpace(def.Description))
                    {
                        error = "Every worker definition requires a non-empty description";
                        return null;
                    }
                    list.Add(def);
                }
                error = null;
                return list;
            }

            // Single object — wrap into a list for convenience.
            if (defsEl.ValueKind == JsonValueKind.Object)
            {
                var def = JsonSerializer.Deserialize<WorkerDefinition>(defsEl.GetRawText(), JsonInput);
                if (def is null)
                {
                    error = "Worker definition deserialised to null";
                    return null;
                }
                if (string.IsNullOrWhiteSpace(def.Description))
                {
                    error = "Worker definition requires a non-empty description";
                    return null;
                }
                error = null;
                return [def];
            }

            error = "definitions must be an array of worker definitions";
            return null;
        }
        catch (JsonException ex)
        {
            error = $"Invalid worker definition: {ex.Message}";
            return null;
        }
    }

    internal static string FormatBatchReceipt(
        WorkerBatchResult batch,
        IReadOnlyDictionary<string, string?> findingsByKey,
        int maxInlineChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"{batch.TotalCount} worker(s) completed ({batch.SucceededCount} ok, {batch.FailedCount} failed, {batch.TotalDuration.TotalSeconds:F1}s total):");
        sb.AppendLine();
        sb.AppendLine($"Batch id: {batch.BatchId}");
        sb.AppendLine();

        var inlined = findingsByKey.Count > 0;
        if (inlined)
        {
            sb.AppendLine("Findings — this is the workers' actual output. Use it directly; do not");
            sb.AppendLine("report that results are unavailable when content appears below.");
            sb.AppendLine();

            foreach (var result in batch.Results)
                AppendFinding(sb, result, findingsByKey, maxInlineChars);
        }

        sb.AppendLine(inlined
            ? "Receipts (WorkerResult JSON — counts, blocked items, converged patterns):"
            : "Receipts (the WorkerResult JSON is below; each worker's findings live at its result_key — fetch with get_from_working_memory):");
        sb.AppendLine(JsonSerializer.Serialize(batch, JsonOutput));
        return sb.ToString().TrimEnd();
    }

    private static void AppendFinding(
        StringBuilder sb,
        WorkerResult result,
        IReadOnlyDictionary<string, string?> findingsByKey,
        int maxInlineChars)
    {
        var key = result.ResultKey;

        if (!findingsByKey.TryGetValue(key, out var content))
        {
            sb.AppendLine($"--- {result.TaskId} ({key}) — NOT RETRIEVED ---");
            sb.AppendLine(
                $"Working memory could not be read for this key. Call get_from_working_memory('{key}') " +
                "to read this worker's findings before answering.");
            sb.AppendLine();
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            sb.AppendLine($"--- {result.TaskId} ({key}) — NO CONTENT ---");
            sb.AppendLine("The worker wrote nothing to this key. Do not report findings for this worker.");
            sb.AppendLine();
            return;
        }

        if (maxInlineChars > 0 && content.Length > maxInlineChars)
        {
            sb.AppendLine(
                $"--- {result.TaskId} ({key}) — {content.Length:N0} chars, first {maxInlineChars:N0} shown ---");
            sb.AppendLine(content[..maxInlineChars]);
            sb.AppendLine(
                $"[truncated] Call get_from_working_memory('{key}') NOW to read the full value before answering.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"--- {result.TaskId} ({key}) ---");
        sb.AppendLine(content);
        sb.AppendLine();
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}

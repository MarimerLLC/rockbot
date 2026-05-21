using System.Text;
using System.Text.Json;
using RockBot.Tools;

namespace RockBot.Subagent.Worker;

/// <summary>
/// Tool executor for <c>spawn_workers</c>. Accepts an array of worker definitions,
/// runs them concurrently via <see cref="IWorkerManager"/>, and returns a batch
/// JSON receipt — one <see cref="WorkerResult"/> per definition.
/// </summary>
internal sealed class SpawnWorkersExecutor(IWorkerManager manager) : IToolExecutor
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

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = FormatBatchReceipt(batch),
            IsError = false,
        };
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

    internal static string FormatBatchReceipt(WorkerBatchResult batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"{batch.TotalCount} worker(s) completed ({batch.SucceededCount} ok, {batch.FailedCount} failed, {batch.TotalDuration.TotalSeconds:F1}s total):");
        sb.AppendLine();
        sb.AppendLine($"Batch id: {batch.BatchId}");
        sb.AppendLine();
        sb.AppendLine("Receipts (each result_key holds the structured WorkerResult JSON — fetch with get_from_working_memory):");
        sb.AppendLine(JsonSerializer.Serialize(batch, JsonOutput));
        return sb.ToString().TrimEnd();
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}

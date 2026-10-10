using System.Text.Json;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Subagent;

internal sealed class SpawnSubagentExecutor(
    ISubagentManager manager,
    SubagentInputResolver? inputResolver = null) : IToolExecutor
{
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

        if (!args.TryGetValue("description", out var descEl) || descEl.ValueKind != JsonValueKind.String)
            return Error(request, "Missing required argument: description");

        var description = descEl.GetString()!;
        var context = args.TryGetValue("context", out var ctxEl) ? ctxEl.GetString() : null;
        int? timeoutMinutes = args.TryGetValue("timeout_minutes", out var toEl) && toEl.TryGetInt32(out var to) ? to : null;
        int? maxIterations = args.TryGetValue("max_iterations", out var miEl) && miEl.TryGetInt32(out var mi) ? mi : null;
        bool consolidate = !args.TryGetValue("consolidate", out var consEl) || consEl.ValueKind != JsonValueKind.False;

        var primarySessionId = request.SessionId ?? "unknown";

        // #666: the user request the spawning loop serves (set by AgentLoopRunner.RunAsync on this
        // async flow). It goes with the task so the synthesis turn is judged against what the user
        // asked, not just against the description the primary wrote for the subagent.
        var originatingUserRequest = OriginatingUserRequestContext.Value;

        // #665: keys and files the subagent must use, validated now so a wrong key fails the spawn
        // with the names that do exist instead of being invented later.
        IReadOnlyList<SubagentInput>? inputs = null;
        var inputRefs = ParseInputs(args);
        if (inputRefs.Count > 0)
        {
            if (inputResolver is null)
                return Error(request, "spawn_subagent inputs are not available in this host; describe the data in context instead.");
            var resolution = await inputResolver.ResolveAsync(inputRefs, request.SessionId, ct);
            if (resolution.Error is not null)
                return Error(request, resolution.Error);
            inputs = resolution.Inputs;
        }

        var taskId = await manager.SpawnAsync(description, context, timeoutMinutes, primarySessionId, ct,
            batchId: request.BatchId, consolidate: consolidate, maxIterations: maxIterations,
            originatingUserRequest: originatingUserRequest, inputs: inputs);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = taskId.StartsWith("Error:")
                ? taskId
                : $"Subagent spawned with task_id: {taskId}. It will report progress and send a final result when complete.",
            IsError = taskId.StartsWith("Error:")
        };
    }

    /// <summary>
    /// The <c>inputs</c> argument as a list of strings. Accepts an array, a single string, or a
    /// JSON-array string (some models stringify array arguments).
    /// </summary>
    internal static IReadOnlyList<string> ParseInputs(Dictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("inputs", out var el)) return [];
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                return el.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            case JsonValueKind.String:
                var text = el.GetString()?.Trim();
                if (string.IsNullOrEmpty(text)) return [];
                if (text.StartsWith('['))
                {
                    try
                    {
                        return JsonSerializer.Deserialize<List<string>>(text)?
                            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];
                    }
                    catch (JsonException) { /* fall through: treat as one reference */ }
                }
                return [text];
            default:
                return [];
        }
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}

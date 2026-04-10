using System.Text;
using System.Text.Json;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Tool executor for <c>spawn_wisp</c>. Parses a wisp JSON definition from the tool
/// arguments, executes it synchronously via <see cref="WispExecutor"/>, and returns
/// structured results including per-step success/failure and failure classification.
/// </summary>
internal sealed class SpawnWispExecutor(WispExecutor wispExecutor) : IToolExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
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

        // Parse the wisp definition — accept either a nested JSON object or a JSON string
        WispDefinition? definition;
        if (!args.TryGetValue("definition", out var defEl))
            return Error(request, "Missing required argument: definition");

        try
        {
            if (defEl.ValueKind == JsonValueKind.String)
            {
                definition = JsonSerializer.Deserialize<WispDefinition>(defEl.GetString()!, JsonOptions);
            }
            else
            {
                definition = JsonSerializer.Deserialize<WispDefinition>(defEl.GetRawText(), JsonOptions);
            }
        }
        catch (JsonException ex)
        {
            return Error(request, $"Invalid wisp definition: {ex.Message}");
        }

        if (definition is null)
            return Error(request, "Wisp definition deserialized to null");

        if (definition.Steps is null or { Count: 0 })
            return Error(request, "Wisp definition must contain at least one step");

        // Generate a unique wisp ID
        var wispId = $"wisp-{Guid.NewGuid():N}"[..16];

        // Execute synchronously — wisps block until all steps complete
        var result = await wispExecutor.ExecuteAsync(definition, wispId, ct);

        // Format the response
        var content = FormatResult(result);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = !result.IsSuccess
        };
    }

    internal static string FormatResult(WispExecutionResult result)
    {
        var sb = new StringBuilder();

        if (result.IsSuccess)
        {
            sb.AppendLine($"Wisp `{result.WispId}` completed successfully ({result.StepResults.Count} steps, {result.Duration.TotalMilliseconds:F0}ms).");
        }
        else
        {
            var failed = result.FailedStep;
            sb.AppendLine($"Wisp `{result.WispId}` failed at step `{failed?.StepId}` (index {failed?.StepIndex}).");
            if (failed?.Error is not null)
            {
                sb.AppendLine($"Error category: {failed.Error.Category}");
                sb.AppendLine($"Error: {failed.Error.Message}");
                if (failed.Error.ToolName is not null)
                    sb.AppendLine($"Tool: {failed.Error.ToolName}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Step Results");
        sb.AppendLine();

        foreach (var step in result.StepResults)
        {
            var status = step.WasSkipped ? "skipped"
                : step.FailureHandled ? "failed (handled)"
                : step.IsSuccess ? "ok"
                : "failed";

            sb.AppendLine($"- **{step.StepId}** [{status}] ({step.Duration.TotalMilliseconds:F0}ms)");

            if (step.IsSuccess && step.Content is not null)
            {
                // Truncate long content for the summary
                var preview = step.Content.Length > 500
                    ? step.Content[..500] + $"... ({step.Content.Length:N0} chars total)"
                    : step.Content;
                sb.AppendLine($"  Output: {preview}");
            }

            if (step.Error is not null)
            {
                sb.AppendLine($"  Error ({step.Error.Category}): {step.Error.Message}");
            }
        }

        // Mention working memory namespace for result retrieval
        sb.AppendLine();
        sb.AppendLine($"Working memory namespace: `wisp/{result.WispId}`");
        if (!result.IsSuccess)
            sb.AppendLine("Working memory preserved for debugging (not cleaned up on failure).");

        return sb.ToString().TrimEnd();
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}

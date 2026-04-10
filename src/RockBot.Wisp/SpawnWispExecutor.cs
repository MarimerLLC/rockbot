using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Tool executor for <c>spawn_wisp</c>. Parses a wisp JSON definition from the tool
/// arguments, executes it synchronously via <see cref="WispExecutor"/>, and returns
/// structured results including per-step success/failure and failure classification.
/// Logs every execution to <see cref="IWispExecutionLog"/> and detects correction pairs
/// when a caller retries a previously failed wisp definition.
/// </summary>
internal sealed class SpawnWispExecutor(
    WispExecutor wispExecutor,
    IWispExecutionLog? executionLog,
    IFeedbackStore? feedbackStore,
    ILogger<SpawnWispExecutor> logger) : IToolExecutor
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
        string? definitionJson;
        if (!args.TryGetValue("definition", out var defEl))
            return Error(request, "Missing required argument: definition");

        try
        {
            if (defEl.ValueKind == JsonValueKind.String)
            {
                definitionJson = defEl.GetString()!;
                definition = JsonSerializer.Deserialize<WispDefinition>(definitionJson, JsonOptions);
            }
            else
            {
                definitionJson = defEl.GetRawText();
                definition = JsonSerializer.Deserialize<WispDefinition>(definitionJson, JsonOptions);
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

        // Generate a unique wisp ID and compute definition hash for retry detection
        var wispId = $"wisp-{Guid.NewGuid():N}"[..16];
        var defHash = ComputeDefinitionHash(definitionJson);

        // Execute synchronously — wisps block until all steps complete
        var result = await wispExecutor.ExecuteAsync(definition, wispId, ct);

        // Log execution and detect correction pairs (fire-and-forget, don't block the response)
        _ = LogExecutionAsync(result, defHash, request.SessionId, ct);

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

    private async Task LogExecutionAsync(
        WispExecutionResult result, string defHash, string? sessionId, CancellationToken ct)
    {
        if (executionLog is null)
            return;

        try
        {
            // Check for prior failure with same definition hash (retry detection)
            string? retryOf = null;
            if (result.IsSuccess)
            {
                var priorFailure = await executionLog.FindRecentFailureAsync(defHash, sessionId, ct);
                if (priorFailure is not null)
                {
                    retryOf = priorFailure.WispId;
                    logger.LogInformation(
                        "Wisp {WispId} detected as successful retry of failed wisp {PriorWispId}",
                        result.WispId, priorFailure.WispId);

                    // Emit a correction pair feedback signal
                    if (feedbackStore is not null)
                    {
                        var detail = JsonSerializer.Serialize(new
                        {
                            priorWispId = priorFailure.WispId,
                            correctedWispId = result.WispId,
                            priorFailureCategory = priorFailure.FailureCategory,
                            priorErrorMessage = priorFailure.ErrorMessage,
                            priorFailedStep = priorFailure.FailedStepId,
                            description = result.Definition.Description
                        }, JsonOptions);

                        await feedbackStore.AppendAsync(new FeedbackEntry(
                            Id: $"wisp-correction-{result.WispId}",
                            SessionId: sessionId ?? "unknown",
                            SignalType: FeedbackSignalType.WispCorrection,
                            Summary: $"Wisp retry succeeded: '{result.Definition.Description}' " +
                                     $"(prior failure: {priorFailure.FailureCategory} at step {priorFailure.FailedStepId})",
                            Detail: detail,
                            Timestamp: DateTimeOffset.UtcNow), ct);
                    }
                }
            }

            var failedStep = result.FailedStep;
            var record = new WispExecutionRecord
            {
                WispId = result.WispId,
                Description = result.Definition.Description,
                DefinitionHash = defHash,
                Succeeded = result.IsSuccess,
                StepCount = result.Definition.Steps.Count,
                StepsCompleted = result.StepResults.Count(s => s.IsSuccess || s.WasSkipped),
                FailedStepId = failedStep?.StepId,
                FailedStepIndex = failedStep?.StepIndex,
                FailureCategory = failedStep?.Error?.Category.ToString(),
                ErrorMessage = failedStep?.Error?.Message,
                FailedToolName = failedStep?.Error?.ToolName,
                DurationMs = (int)result.Duration.TotalMilliseconds,
                Timestamp = DateTimeOffset.UtcNow,
                SessionId = sessionId,
                RetryOf = retryOf
            };

            await executionLog.AppendAsync(record, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to log wisp execution record for {WispId}", result.WispId);
        }
    }

    internal static string ComputeDefinitionHash(string definitionJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(definitionJson));
        return Convert.ToHexStringLower(bytes)[..16];
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

        sb.AppendLine();
        sb.AppendLine($"Working memory namespace: `wisp/{result.WispId}`");
        if (!result.IsSuccess)
            sb.AppendLine("Working memory preserved for debugging (not cleaned up on failure).");

        return sb.ToString().TrimEnd();
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Tool executor for <c>spawn_wisps</c>. Accepts an array of wisp definitions,
/// executes them concurrently (up to <see cref="WispOptions.MaxConcurrentWisps"/>),
/// and returns a batch result with per-wisp success/failure.
/// Always writes a batch summary to working memory for downstream consumption.
/// </summary>
internal sealed class SpawnWispsExecutor(
    WispExecutor wispExecutor,
    IWispExecutionLog? executionLog,
    IFeedbackStore? feedbackStore,
    IWorkingMemory workingMemory,
    WispOptions options,
    ILogger<SpawnWispsExecutor> logger) : IToolExecutor
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

        // Parse the definitions array
        var definitions = ParseDefinitions(args, out var parseError);
        if (definitions is null)
            return Error(request, parseError!);

        if (definitions.Count == 0)
            return Error(request, "definitions array must contain at least one wisp definition");

        // Generate batch ID
        var batchId = $"batch-{Guid.NewGuid():N}"[..18];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Execute all wisps concurrently, gated by the configured concurrency limit
        using var semaphore = new SemaphoreSlim(options.MaxConcurrentWisps);
        var tasks = definitions.Select(def => ExecuteOneAsync(def, batchId, request.SessionId, semaphore, ct)).ToList();
        var results = await Task.WhenAll(tasks);

        stopwatch.Stop();

        var batchResult = new WispBatchResult
        {
            BatchId = batchId,
            Results = results,
            TotalDuration = stopwatch.Elapsed
        };

        // Write batch summary to working memory
        await WriteBatchSummaryAsync(batchResult, ct);

        // Format response
        var content = FormatBatchResult(batchResult);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = false
        };
    }

    private async Task<WispExecutionResult> ExecuteOneAsync(
        WispDefinition definition,
        string batchId,
        string? sessionId,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var wispId = $"wisp-{Guid.NewGuid():N}"[..16];
            var defJson = JsonSerializer.Serialize(definition, JsonOptions);
            var defHash = ComputeDefinitionHash(defJson);

            var result = await wispExecutor.ExecuteAsync(definition, wispId, ct);

            // Log execution (fire-and-forget, don't block the batch)
            _ = LogExecutionAsync(result, defHash, batchId, sessionId, ct);

            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task LogExecutionAsync(
        WispExecutionResult result, string defHash, string batchId, string? sessionId, CancellationToken ct)
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
                RetryOf = retryOf,
                BatchId = batchId
            };

            await executionLog.AppendAsync(record, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to log wisp execution record for {WispId}", result.WispId);
        }
    }

    private async Task WriteBatchSummaryAsync(WispBatchResult batch, CancellationToken ct)
    {
        try
        {
            var summary = new
            {
                batchId = batch.BatchId,
                total = batch.TotalCount,
                succeeded = batch.SucceededCount,
                failed = batch.FailedCount,
                durationMs = (int)batch.TotalDuration.TotalMilliseconds,
                wisps = batch.Results.Select(r =>
                {
                    // Include the last successful step's output (typically the summary/final result)
                    var lastOutput = r.StepResults
                        .LastOrDefault(s => s.IsSuccess && s.Content is not null)?.Content;

                    return new
                    {
                        wispId = r.WispId,
                        description = r.Definition.Description,
                        succeeded = r.IsSuccess,
                        durationMs = (int)r.Duration.TotalMilliseconds,
                        stepsCompleted = r.StepResults.Count(s => s.IsSuccess || s.WasSkipped),
                        stepCount = r.Definition.Steps.Count,
                        output = lastOutput is not null
                            ? (lastOutput.Length > 2_000 ? lastOutput[..2_000] + "..." : lastOutput)
                            : null,
                        error = r.FailedStep is { Error: not null }
                            ? new { category = r.FailedStep.Error.Category.ToString(), message = r.FailedStep.Error.Message }
                            : null
                    };
                }).ToList()
            };

            var json = JsonSerializer.Serialize(summary, JsonOptions);
            var key = $"wisp/batch-{batch.BatchId}/summary";
            await workingMemory.SetAsync(key, json, ttl: TimeSpan.FromMinutes(60), category: "wisp-batch");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write batch summary to working memory for batch {BatchId}", batch.BatchId);
        }
    }

    private static IReadOnlyList<WispDefinition>? ParseDefinitions(
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
                var definitions = new List<WispDefinition>();
                foreach (var item in defsEl.EnumerateArray())
                {
                    var json = item.ValueKind == JsonValueKind.String
                        ? item.GetString()!
                        : item.GetRawText();

                    var def = JsonSerializer.Deserialize<WispDefinition>(json, JsonOptions);
                    if (def is null)
                    {
                        error = "A wisp definition deserialized to null";
                        return null;
                    }

                    if (def.Steps is null or { Count: 0 })
                    {
                        error = $"Wisp definition '{def.Description ?? "(no description)"}' must contain at least one step";
                        return null;
                    }

                    definitions.Add(def);
                }

                error = null;
                return definitions;
            }

            // Single object — wrap in a list for convenience
            if (defsEl.ValueKind == JsonValueKind.Object)
            {
                var json = defsEl.GetRawText();
                var def = JsonSerializer.Deserialize<WispDefinition>(json, JsonOptions);
                if (def is null)
                {
                    error = "Wisp definition deserialized to null";
                    return null;
                }

                if (def.Steps is null or { Count: 0 })
                {
                    error = "Wisp definition must contain at least one step";
                    return null;
                }

                error = null;
                return [def];
            }

            error = "definitions must be an array of wisp definitions";
            return null;
        }
        catch (JsonException ex)
        {
            error = $"Invalid wisp definition: {ex.Message}";
            return null;
        }
    }

    internal static string ComputeDefinitionHash(string definitionJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(definitionJson));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    internal static string FormatBatchResult(WispBatchResult batch)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"{batch.TotalCount} wisp(s) completed ({batch.SucceededCount} succeeded, {batch.FailedCount} failed, {batch.TotalDuration.TotalSeconds:F1}s total):");
        sb.AppendLine();

        foreach (var result in batch.Results)
        {
            var status = result.IsSuccess ? "ok" : "failed";
            sb.AppendLine($"- `{result.WispId}`: \"{result.Definition.Description}\" [{status}] ({result.Duration.TotalMilliseconds:F0}ms)");

            if (!result.IsSuccess && result.FailedStep?.Error is { } err)
            {
                sb.AppendLine($"  Error ({err.Category}): {err.Message}");
                if (err.ToolName is not null)
                    sb.AppendLine($"  Tool: {err.ToolName}");
            }

            if (result.IsSuccess)
            {
                // Show last step output for successful wisps — this is the primary
                // way the calling agent consumes wisp results, so don't truncate too aggressively
                var lastStep = result.StepResults.LastOrDefault(s => s.IsSuccess && s.Content is not null);
                if (lastStep?.Content is not null)
                {
                    var preview = lastStep.Content.Length > 2_000
                        ? lastStep.Content[..2_000] + $"... ({lastStep.Content.Length:N0} chars total)"
                        : lastStep.Content;
                    sb.AppendLine($"  Output: {preview}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Batch ID: `{batch.BatchId}`");
        sb.AppendLine($"Batch summary: `wisp/batch-{batch.BatchId}/summary`");

        return sb.ToString().TrimEnd();
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}

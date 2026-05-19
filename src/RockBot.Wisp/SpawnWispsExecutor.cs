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
    ILogger<SpawnWispsExecutor> logger,
    ISkillStore? skillStore = null,
    ISkillUsageStore? skillUsageStore = null) : IToolExecutor
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
            var shapeHash = ComputeShapeHash(definition);

            var result = await wispExecutor.ExecuteAsync(definition, wispId, parentSessionId: sessionId, ct);

            // Log execution (fire-and-forget, don't block the batch)
            _ = LogExecutionAsync(result, defHash, shapeHash, defJson, batchId, sessionId, ct);

            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Maximum byte size of a step-definition body retained on a successful
    /// wisp execution record. Bodies larger than this are dropped (with a
    /// diagnostic flag) so the JSONL log does not bloat from oversize runs.
    /// </summary>
    internal const int DefinitionBodyMaxBytes = 8 * 1024;

    private async Task LogExecutionAsync(
        WispExecutionResult result, string defHash, string shapeHash, string defJson,
        string batchId, string? sessionId, CancellationToken ct)
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

            // Retain the JSON step body only on successful runs, and only if it
            // fits the cap. Failed runs leave Body=null (failure context lives on
            // FailureCategory/ErrorMessage). Oversize successes set the diagnostic flag
            // so the dream pass treats them as ineligible for promotion.
            string? definitionBody = null;
            bool bodyOmittedTooLarge = false;
            if (result.IsSuccess)
            {
                if (Encoding.UTF8.GetByteCount(defJson) <= DefinitionBodyMaxBytes)
                    definitionBody = defJson;
                else
                    bodyOmittedTooLarge = true;
            }

            var failedStep = result.FailedStep;
            var record = new WispExecutionRecord
            {
                WispId = result.WispId,
                Description = result.Definition.Description,
                DefinitionHash = defHash,
                ShapeHash = shapeHash,
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
                BatchId = batchId,
                DefinitionBody = definitionBody,
                BodyOmittedTooLarge = bodyOmittedTooLarge
            };

            await executionLog.AppendAsync(record, ct);

            if (result.IsSuccess)
                await TryEagerPromoteAsync(record, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to log wisp execution record for {WispId}", result.WispId);
        }
    }

    /// <summary>
    /// When a scheduled-task wisp succeeds and the same shape has already
    /// succeeded enough times, attach its body to the originating skill as a
    /// provisional <c>Wisp</c> resource. Bypasses the slower dream-pass promotion
    /// path so daily/weekly scheduled work captures a reusable asset after only
    /// a couple of fires.
    ///
    /// Best-effort: any failure logs and returns without disturbing the wisp run.
    /// </summary>
    private async Task TryEagerPromoteAsync(WispExecutionRecord record, CancellationToken ct)
    {
        if (!options.EagerScheduledTaskPromotionEnabled
            || skillStore is null
            || executionLog is null
            || string.IsNullOrEmpty(record.SessionId)
            || string.IsNullOrEmpty(record.ShapeHash)
            || string.IsNullOrEmpty(record.DefinitionBody))
            return;

        if (!record.SessionId.StartsWith(options.ScheduledTaskSessionPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            // Count prior successful runs of this shape (including the one just
            // appended). The 30-day window matches the dream-pass horizon.
            var recent = await executionLog.QueryRecentAsync(
                DateTimeOffset.UtcNow.AddDays(-30), maxResults: 1000, ct);
            var sameShape = recent
                .Where(r => r.Succeeded && string.Equals(r.ShapeHash, record.ShapeHash, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var threshold = Math.Max(1, options.EagerScheduledTaskPromotionThreshold);
            if (sameShape.Count < threshold)
                return;

            // Resolve invoking skill the same way the dream pass does — most recent
            // skill invocation in the session that precedes the wisp run.
            var invokingSkill = await ResolveInvokingSkillAsync(record, ct);
            if (string.IsNullOrEmpty(invokingSkill))
                return;

            // Don't re-attach if the same shape is already on the skill.
            var skill = await skillStore.GetAsync(invokingSkill);
            if (skill is null)
                return;
            if (skill.Manifest?.Any(r =>
                    string.Equals(r.DefinitionHash, record.ShapeHash, StringComparison.OrdinalIgnoreCase)) == true)
                return;

            var filename = $"eager-{record.ShapeHash[..Math.Min(8, record.ShapeHash.Length)]}.json";
            var description = $"Scheduled-task pattern: {record.Description}";
            var entry = new SkillResource(
                Filename: filename,
                Type: SkillResourceType.Wisp,
                Description: description,
                Provisional: true,
                CreatedAt: DateTimeOffset.UtcNow,
                VerifyHint: $"Repeats shape {record.ShapeHash} from scheduled-task session {record.SessionId}",
                DefinitionHash: record.ShapeHash);
            var input = new SkillResourceInput(
                filename, SkillResourceType.Wisp, description, record.DefinitionBody!,
                Provisional: true);

            var attached = await skillStore.AttachResourceAsync(invokingSkill, input, entry);
            if (attached)
            {
                logger.LogInformation(
                    "Eager promotion attached '{Filename}' to skill '{Skill}' (shape={Shape}, successes={Count}, session={Session})",
                    filename, invokingSkill, record.ShapeHash, sameShape.Count, record.SessionId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Eager promotion failed for wisp {WispId} (session={Session})",
                record.WispId, record.SessionId);
        }
    }

    private async Task<string?> ResolveInvokingSkillAsync(WispExecutionRecord record, CancellationToken ct)
    {
        if (skillUsageStore is null || skillStore is null || string.IsNullOrEmpty(record.SessionId))
            return null;

        var events = await skillUsageStore.GetBySessionAsync(record.SessionId!, ct);
        if (events.Count == 0)
            return null;

        var existingNames = (await skillStore.ListAsync())
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return events
            .Where(e => e.Timestamp <= record.Timestamp && existingNames.Contains(e.SkillName))
            .OrderByDescending(e => e.Timestamp)
            .Select(e => e.SkillName)
            .FirstOrDefault();
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

    /// <summary>
    /// Hash the structural shape of a wisp — gateway/server/tool/mode plus the
    /// sorted set of parameter keys per step — while stripping description,
    /// prompt text, and literal parameter values. Two wisps that differ only by
    /// description or by date/accountId values share the same shape hash, so
    /// the success-promotion pass can group repeated patterns that the LLM
    /// authored as cosmetically distinct invocations.
    /// </summary>
    internal static string ComputeShapeHash(WispDefinition definition)
    {
        var canonical = new
        {
            steps = definition.Steps.Select(s => new
            {
                id = s.Id,
                mode = s.Mode.ToString(),
                gateway = s.Gateway?.ToString(),
                server = s.Server,
                tool = s.Tool,
                language = s.Language,
                agent = s.Agent,
                skill = s.Skill,
                hasPrompt = !string.IsNullOrEmpty(s.Prompt),
                hasMessage = !string.IsNullOrEmpty(s.Message),
                paramKeys = ExtractParamKeys(s.ResolvedParams),
                metadataKeys = ExtractParamKeys(s.Metadata),
                hasInputFrom = !string.IsNullOrEmpty(s.InputFrom),
                hasOutputTo = !string.IsNullOrEmpty(s.OutputTo),
                onFailure = s.OnFailure?.Action.ToString()
            }).ToList()
        };

        var json = JsonSerializer.Serialize(canonical, JsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    private static IReadOnlyList<string> ExtractParamKeys(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
            return [];

        var keys = new List<string>();
        foreach (var prop in element.Value.EnumerateObject())
            keys.Add(prop.Name);

        keys.Sort(StringComparer.Ordinal);
        return keys;
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

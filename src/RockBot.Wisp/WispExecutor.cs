using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Memory;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Executes a <see cref="WispDefinition"/> — a harness-native pipeline with optional LLM steps.
/// Direct steps invoke tools with zero LLM tokens; LLM steps use a minimal context with
/// restricted tool scope.
/// </summary>
internal sealed class WispExecutor(
    IToolRegistry toolRegistry,
    IWorkingMemory workingMemory,
    AgentLoopRunner agentLoopRunner,
    WispOptions options,
    ILogger<WispExecutor> logger)
{
    private const int DefaultLlmStepMaxIterations = 10;
    private const int InputChunkingThreshold = 8_000;
    private const int ChunkMaxLength = 20_000;
    private static readonly TimeSpan WispChunkTtl = TimeSpan.FromMinutes(30);

    internal static readonly string WispDirectives =
        """
        You are a wisp — a lightweight execution step in a larger pipeline.
        Execute the task described in the user message using only the tools provided.
        Rules:
        - Call only the tools listed in your tool set
        - Do not improvise additional steps beyond what is asked
        - Report the result of your work concisely
        - If a tool call fails, stop and return the error
        - Use working memory to store intermediate results when instructed
        """;

    /// <summary>
    /// Executes all steps in the wisp definition sequentially, returning a structured result.
    /// </summary>
    public async Task<WispExecutionResult> ExecuteAsync(
        WispDefinition definition,
        string wispId,
        CancellationToken ct)
    {
        logger.LogInformation("Wisp {WispId} starting: {Description} ({StepCount} steps)",
            wispId, definition.Description, definition.Steps.Count);

        var overallSw = Stopwatch.StartNew();
        var stepResults = new List<WispStepResult>();
        var resultsByStepId = new Dictionary<string, WispStepResult>(StringComparer.OrdinalIgnoreCase);
        var wispNamespace = $"wisp/{wispId}";
        var skipToStepId = (string?)null;

        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];

            // Handle skip_to from a prior step's on_failure
            if (skipToStepId is not null)
            {
                if (!string.Equals(step.Id, skipToStepId, StringComparison.OrdinalIgnoreCase))
                {
                    var skippedResult = new WispStepResult
                    {
                        StepId = step.Id,
                        StepIndex = i,
                        IsSuccess = true,
                        WasSkipped = true,
                        Duration = TimeSpan.Zero
                    };
                    stepResults.Add(skippedResult);
                    resultsByStepId[step.Id] = skippedResult;
                    continue;
                }
                skipToStepId = null; // Reached the target step, resume normal execution
            }

            logger.LogInformation("Wisp {WispId} step {StepIndex}/{StepCount}: {StepId} (mode={Mode})",
                wispId, i + 1, definition.Steps.Count, step.Id, step.Mode);

            var stepSw = Stopwatch.StartNew();
            WispStepResult stepResult;

            try
            {
                stepResult = step.Mode switch
                {
                    StepMode.Direct => await ExecuteDirectStepAsync(step, i, wispId, wispNamespace, resultsByStepId, ct),
                    StepMode.Llm => await ExecuteLlmStepAsync(step, i, definition, wispId, wispNamespace, resultsByStepId, ct),
                    _ => new WispStepResult
                    {
                        StepId = step.Id,
                        StepIndex = i,
                        IsSuccess = false,
                        Error = new WispStepError
                        {
                            Category = FailureCategory.Structural,
                            Message = $"Unknown step mode: {step.Mode}"
                        },
                        Duration = stepSw.Elapsed
                    }
                };
            }
            catch (OperationCanceledException)
            {
                stepResult = new WispStepResult
                {
                    StepId = step.Id,
                    StepIndex = i,
                    IsSuccess = false,
                    Error = new WispStepError
                    {
                        Category = FailureCategory.External,
                        Message = "Step was cancelled"
                    },
                    Duration = stepSw.Elapsed
                };
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Wisp {WispId} step {StepId} threw unexpected exception", wispId, step.Id);
                stepResult = new WispStepResult
                {
                    StepId = step.Id,
                    StepIndex = i,
                    IsSuccess = false,
                    Error = new WispStepError
                    {
                        Category = FailureCategory.External,
                        Message = ex.Message
                    },
                    Duration = stepSw.Elapsed
                };
            }

            stepResults.Add(stepResult);
            resultsByStepId[step.Id] = stepResult;

            if (!stepResult.IsSuccess)
            {
                // Handle on_failure branching for direct steps
                if (step.Mode == StepMode.Direct && step.OnFailure is not null)
                {
                    if (string.Equals(step.OnFailure.Action, "skip_to", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrEmpty(step.OnFailure.SkipTo))
                    {
                        logger.LogInformation("Wisp {WispId} step {StepId} failed, skipping to {SkipTo}",
                            wispId, step.Id, step.OnFailure.SkipTo);
                        // Mark the failure as handled so it doesn't count against overall success
                        stepResults[^1] = stepResult with { FailureHandled = true };
                        resultsByStepId[step.Id] = stepResults[^1];
                        skipToStepId = step.OnFailure.SkipTo;
                        continue;
                    }
                }

                // Default: abort on failure
                logger.LogWarning("Wisp {WispId} aborting at step {StepId}: {Error}",
                    wispId, step.Id, stepResult.Error?.Message);
                break;
            }

            logger.LogInformation("Wisp {WispId} step {StepId} completed in {Duration:F1}ms",
                wispId, step.Id, stepResult.Duration.TotalMilliseconds);
        }

        overallSw.Stop();
        var isSuccess = stepResults.All(s => s.IsSuccess || s.WasSkipped || s.FailureHandled);

        // On success, shorten TTL so results stay available briefly for the calling agent
        // to inspect, then expire naturally. On failure, keep full TTL for debugging.
        if (isSuccess)
        {
            var entries = await workingMemory.ListAsync(wispNamespace);
            foreach (var entry in entries)
            {
                await workingMemory.SetAsync(entry.Key, entry.Value,
                    ttl: TimeSpan.FromMinutes(5), category: entry.Category, tags: entry.Tags);
            }
            logger.LogDebug("Wisp {WispId} set 5-min TTL on {Count} working memory entries in {Namespace}",
                wispId, entries.Count, wispNamespace);
        }

        logger.LogInformation("Wisp {WispId} finished: success={Success}, duration={Duration:F1}ms",
            wispId, isSuccess, overallSw.Elapsed.TotalMilliseconds);

        return new WispExecutionResult
        {
            WispId = wispId,
            IsSuccess = isSuccess,
            StepResults = stepResults,
            Duration = overallSw.Elapsed,
            Definition = definition
        };
    }

    private async Task<WispStepResult> ExecuteDirectStepAsync(
        WispStep step,
        int index,
        string wispId,
        string wispNamespace,
        IReadOnlyDictionary<string, WispStepResult> priorResults,
        CancellationToken ct)
    {
        var stepSw = Stopwatch.StartNew();

        // Route the step to a tool invocation
        var route = GatewayRouter.Route(step, wispId, priorResults);
        if (!route.IsSuccess)
        {
            return new WispStepResult
            {
                StepId = step.Id,
                StepIndex = index,
                IsSuccess = false,
                Error = new WispStepError
                {
                    Category = route.ErrorCategory ?? FailureCategory.Structural,
                    Message = route.ErrorMessage!
                },
                Duration = stepSw.Elapsed
            };
        }

        // Resolve the executor from the registry
        var executor = toolRegistry.GetExecutor(route.ToolName!);
        if (executor is null)
        {
            return new WispStepResult
            {
                StepId = step.Id,
                StepIndex = index,
                IsSuccess = false,
                Error = new WispStepError
                {
                    Category = FailureCategory.Structural,
                    Message = $"Tool '{route.ToolName}' is not registered",
                    ToolName = route.ToolName
                },
                Duration = stepSw.Elapsed
            };
        }

        // Invoke the tool
        var request = new ToolInvokeRequest
        {
            ToolCallId = $"wisp-{wispId}-{step.Id}",
            ToolName = route.ToolName!,
            Arguments = route.Arguments,
            SessionId = wispId
        };

        var response = await executor.ExecuteAsync(request, ct);
        stepSw.Stop();

        if (response.IsError)
        {
            // Classify: tool returned an error
            var category = ClassifyToolError(response.Content);
            return new WispStepResult
            {
                StepId = step.Id,
                StepIndex = index,
                IsSuccess = false,
                Content = response.Content,
                Error = new WispStepError
                {
                    Category = category,
                    Message = response.Content ?? "Tool returned an error with no message",
                    ToolName = route.ToolName
                },
                Duration = stepSw.Elapsed
            };
        }

        // Always write step output to working memory for inter-step access by LLM steps
        var stepContent = response.Content ?? "";
        var outputKey = $"{wispNamespace}/{step.Id}/output";
        await workingMemory.SetAsync(outputKey, stepContent,
            ttl: TimeSpan.FromMinutes(60), category: "wisp-output");

        // Additionally write to shared volume file if output_to is specified
        if (!string.IsNullOrEmpty(step.OutputTo))
        {
            await WriteToSharedVolumeAsync(step.OutputTo, stepContent, ct);
            logger.LogDebug("Wisp {WispId} step {StepId} wrote output to shared volume: {Path}",
                wispId, step.Id, step.OutputTo);
        }

        return new WispStepResult
        {
            StepId = step.Id,
            StepIndex = index,
            IsSuccess = true,
            Content = response.Content,
            Duration = stepSw.Elapsed
        };
    }

    private async Task<WispStepResult> ExecuteLlmStepAsync(
        WispStep step,
        int index,
        WispDefinition definition,
        string wispId,
        string wispNamespace,
        IReadOnlyDictionary<string, WispStepResult> priorResults,
        CancellationToken ct)
    {
        var stepSw = Stopwatch.StartNew();

        if (string.IsNullOrEmpty(step.Prompt))
        {
            return new WispStepResult
            {
                StepId = step.Id,
                StepIndex = index,
                IsSuccess = false,
                Error = new WispStepError
                {
                    Category = FailureCategory.Structural,
                    Message = "LLM mode steps require a 'prompt' field"
                },
                Duration = stepSw.Elapsed
            };
        }

        // Build minimal chat messages: wisp directives + step prompt
        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, WispDirectives)
        };

        // Handle input_from: inject prior step data or shared volume content into the prompt
        var userPrompt = step.Prompt;
        if (!string.IsNullOrEmpty(step.InputFrom))
        {
            var inputContent = await ResolveInputFromAsync(step.InputFrom, wispNamespace, priorResults, definition, ct);
            if (inputContent is not null)
            {
                if (inputContent.Length <= InputChunkingThreshold)
                {
                    // Small content: inject directly into the prompt
                    userPrompt += $"\n\n## Input Data\n\n{inputContent}";
                }
                else
                {
                    // Large content: chunk into working memory, give LLM the index
                    var chunkIndex = await ChunkIntoWorkingMemoryAsync(
                        inputContent, wispNamespace, step.Id, ct);
                    userPrompt += $"\n\n## Input Data\n\n{chunkIndex}";
                }
            }
        }
        else if (priorResults.Count > 0)
        {
            // No explicit input_from — auto-inject prior step results so the LLM
            // has context without needing to guess at working memory keys
            var priorData = new StringBuilder();
            priorData.AppendLine("## Prior Step Results");
            priorData.AppendLine();
            foreach (var (stepId, prior) in priorResults)
            {
                if (!prior.IsSuccess || prior.WasSkipped || prior.Content is null)
                    continue;
                priorData.AppendLine($"### Step: {stepId}");
                priorData.AppendLine();
                priorData.AppendLine(prior.Content.Length > 4_000
                    ? prior.Content[..4_000] + $"\n\n... (truncated, {prior.Content.Length:N0} chars total — use get_from_working_memory if you need the full content)"
                    : prior.Content);
                priorData.AppendLine();
            }

            var injected = priorData.ToString().Trim();
            if (injected.Length > "## Prior Step Results".Length + 5)
            {
                userPrompt += $"\n\n{injected}";
            }
        }

        chatMessages.Add(new ChatMessage(ChatRole.User, userPrompt));

        // Build scoped tool set for the LLM step
        var scopedTools = BuildLlmStepTools(definition, wispNamespace);

        var chatOptions = new ChatOptions
        {
            Tools = scopedTools
        };

        // Run with minimal AgentLoopRunner config: Low tier, no follow-up, no completion eval
        string llmOutput;
        try
        {
            llmOutput = await agentLoopRunner.RunAsync(
                chatMessages, chatOptions, wispId,
                tier: ModelTier.Low,
                enableFollowUp: false,
                enableCompletionEval: false,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            stepSw.Stop();
            return new WispStepResult
            {
                StepId = step.Id,
                StepIndex = index,
                IsSuccess = false,
                Error = new WispStepError
                {
                    Category = FailureCategory.Judgment,
                    Message = $"LLM step failed: {ex.Message}"
                },
                Duration = stepSw.Elapsed
            };
        }

        stepSw.Stop();

        // Always write step output to working memory for inter-step access
        var llmOutputKey = $"{wispNamespace}/{step.Id}/output";
        await workingMemory.SetAsync(llmOutputKey, llmOutput,
            ttl: TimeSpan.FromMinutes(60), category: "wisp-output");

        // Additionally write to shared volume file if output_to is specified
        if (!string.IsNullOrEmpty(step.OutputTo))
        {
            await WriteToSharedVolumeAsync(step.OutputTo, llmOutput, ct);
            logger.LogDebug("Wisp {WispId} step {StepId} wrote LLM output to shared volume: {Path}",
                wispId, step.Id, step.OutputTo);
        }

        return new WispStepResult
        {
            StepId = step.Id,
            StepIndex = index,
            IsSuccess = true,
            Content = llmOutput,
            Duration = stepSw.Elapsed
        };
    }

    /// <summary>
    /// Builds the scoped tool set for an LLM step. Includes:
    /// - All tools implied by direct steps' gateway declarations
    /// - Tools listed in the top-level 'tools' array
    /// - Working memory tools (GetFromWorkingMemory, SearchWorkingMemory)
    /// </summary>
    private List<AITool> BuildLlmStepTools(WispDefinition definition, string wispNamespace)
    {
        var tools = new List<AITool>();

        // Collect tool names from direct steps and top-level tools array
        var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in definition.Steps.Where(s => s.Mode == StepMode.Direct))
        {
            var name = GatewayRouter.GetToolName(s);
            if (name is not null)
                toolNames.Add(name);
        }

        if (definition.Tools is not null)
        {
            foreach (var name in definition.Tools)
                toolNames.Add(name);
        }

        // Wrap registry tools as AIFunctions
        foreach (var name in toolNames)
        {
            var registration = toolRegistry.GetTools().FirstOrDefault(r =>
                string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            var executor = toolRegistry.GetExecutor(name);

            if (registration is not null && executor is not null)
            {
                tools.Add(new WispRegistryToolFunction(registration, executor, wispId: wispNamespace));
            }
        }

        // Add working memory tools scoped to wisp namespace
        var wmTools = new WorkingMemoryTools(workingMemory, wispNamespace, logger);
        tools.AddRange(wmTools.Tools);

        return tools;
    }

    /// <summary>
    /// Resolves an input_from reference to actual content. Resolution order:
    /// 1. Template references like {{steps.id.result}}
    /// 2. Prior step's working memory output (if a step wrote to this output_to path)
    /// 3. Shared volume file read (if path is a file on the shared volume)
    /// </summary>
    private async Task<string?> ResolveInputFromAsync(
        string inputFrom,
        string wispNamespace,
        IReadOnlyDictionary<string, WispStepResult> priorResults,
        WispDefinition definition,
        CancellationToken ct)
    {
        // 1. Check if it's a template reference like {{steps.id.result}}
        var resolved = GatewayRouter.ResolveTemplateString(inputFrom, priorResults, definition);
        if (resolved != inputFrom)
            return resolved;

        // 2. Check if a prior step wrote to this exact output_to path — use in-memory content
        foreach (var step in definition.Steps)
        {
            if (string.Equals(step.OutputTo, inputFrom, StringComparison.OrdinalIgnoreCase)
                && priorResults.TryGetValue(step.Id, out var priorResult)
                && priorResult.Content is not null)
            {
                return priorResult.Content;
            }
        }

        // 3. Read from shared volume as a file path
        var fileContent = await ReadFromSharedVolumeAsync(inputFrom, ct);
        if (fileContent is not null)
        {
            logger.LogDebug("Wisp read input_from shared volume: {Path} ({Length} chars)",
                inputFrom, fileContent.Length);
            return fileContent;
        }

        return null;
    }

    /// <summary>
    /// Chunks large content into working memory and returns an index table
    /// that the LLM can use with GetFromWorkingMemory to access individual chunks.
    /// </summary>
    private async Task<string> ChunkIntoWorkingMemoryAsync(
        string content, string wispNamespace, string stepId, CancellationToken ct)
    {
        var chunks = ContentChunker.Chunk(content, ChunkMaxLength);
        var runId = Guid.NewGuid().ToString("N")[..8];
        var keyBase = $"{wispNamespace}/input-{stepId}-{runId}";

        var chunkKeys = new List<string>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var key = $"{keyBase}-chunk{i}";
            chunkKeys.Add(key);
            await workingMemory.SetAsync(key, chunks[i].Content, ttl: WispChunkTtl, category: "wisp-input");
        }

        // Store outline index
        var indexKey = $"{keyBase}-index";
        var outline = ContentChunker.BuildOutline(chunks, chunkKeys);
        await workingMemory.SetAsync(indexKey, outline, ttl: WispChunkTtl, category: "wisp-input");

        // Build LLM-friendly index table
        var sb = new StringBuilder();
        sb.AppendLine($"Input data is large ({content.Length:N0} chars) and has been split into {chunks.Count} chunk(s).");
        sb.AppendLine($"A document outline is stored at key `{indexKey}` — retrieve it with get_from_working_memory.");
        sb.AppendLine("Call get_from_working_memory(key) for each relevant chunk BEFORE drawing conclusions.");
        sb.AppendLine();
        sb.AppendLine("| # | Heading | Key |");
        sb.AppendLine("|---|---------|-----|");

        for (var i = 0; i < chunks.Count; i++)
        {
            var label = string.IsNullOrWhiteSpace(chunks[i].Heading) ? $"Part {i}" : chunks[i].Heading;
            sb.AppendLine($"| {i} | {label} | `{chunkKeys[i]}` |");
        }

        logger.LogInformation("Wisp chunked input for step {StepId}: {Length:N0} chars → {Count} chunk(s)",
            stepId, content.Length, chunks.Count);

        return sb.ToString().Trim();
    }

    // ── Shared volume file I/O ─────────────────────────────────────────────

    private async Task WriteToSharedVolumeAsync(string relativePath, string content, CancellationToken ct)
    {
        if (options.SharedVolumePath is null)
            return;

        var fullPath = SafeResolvePath(options.SharedVolumePath, relativePath);
        if (fullPath is null)
        {
            logger.LogWarning("Wisp output_to path escapes shared volume, skipping write: {Path}", relativePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, ct);

        // Make files world-writable so other containers sharing the volume
        // (script pods, MCP servers) can read and overwrite them regardless
        // of which user created them.
        try
        {
            File.SetUnixFileMode(fullPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }
        catch
        {
            // Non-Unix platforms or permission errors — best-effort only.
        }
    }

    private async Task<string?> ReadFromSharedVolumeAsync(string relativePath, CancellationToken ct)
    {
        if (options.SharedVolumePath is null)
            return null;

        var fullPath = SafeResolvePath(options.SharedVolumePath, relativePath);
        if (fullPath is null)
            return null;

        if (!File.Exists(fullPath))
            return null;

        return await File.ReadAllTextAsync(fullPath, ct);
    }

    internal static string? SafeResolvePath(string basePath, string relativePath)
    {
        var fullBase = Path.GetFullPath(basePath);
        var fullPath = Path.GetFullPath(Path.Combine(fullBase, relativePath.TrimEnd('/', '\\')));
        return fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    /// <summary>
    /// Classifies a tool error response into a failure category.
    /// </summary>
    private static FailureCategory ClassifyToolError(string? errorContent)
    {
        if (string.IsNullOrEmpty(errorContent))
            return FailureCategory.External;

        // Structural: tool/param validation errors
        if (errorContent.Contains("Missing required parameter", StringComparison.OrdinalIgnoreCase)
            || errorContent.Contains("not registered", StringComparison.OrdinalIgnoreCase)
            || errorContent.Contains("Unknown tool", StringComparison.OrdinalIgnoreCase)
            || errorContent.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            return FailureCategory.Structural;

        // External: transient/network errors
        if (errorContent.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || errorContent.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || errorContent.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || errorContent.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || errorContent.Contains("503", StringComparison.Ordinal)
            || errorContent.Contains("429", StringComparison.Ordinal))
            return FailureCategory.External;

        // Data: format/schema mismatches
        if (errorContent.Contains("unexpected format", StringComparison.OrdinalIgnoreCase)
            || errorContent.Contains("empty result", StringComparison.OrdinalIgnoreCase)
            || errorContent.Contains("parse", StringComparison.OrdinalIgnoreCase)
            || errorContent.Contains("schema", StringComparison.OrdinalIgnoreCase))
            return FailureCategory.Data;

        return FailureCategory.External;
    }
}

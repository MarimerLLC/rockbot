using System.Diagnostics;
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
    ILogger<WispExecutor> logger)
{
    private const int DefaultLlmStepMaxIterations = 10;

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

        // Write output to working memory if output_to is specified
        if (!string.IsNullOrEmpty(step.OutputTo))
        {
            var outputKey = $"{wispNamespace}/{step.Id}/output";
            await workingMemory.SetAsync(outputKey, response.Content ?? "",
                ttl: TimeSpan.FromMinutes(60), category: "wisp-output");
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

        // Handle input_from: inject prior step data into the prompt
        var userPrompt = step.Prompt;
        if (!string.IsNullOrEmpty(step.InputFrom))
        {
            var inputContent = ResolveInputFrom(step.InputFrom, wispNamespace, priorResults);
            if (inputContent is not null)
            {
                userPrompt += $"\n\n## Input Data\n\n{inputContent}";
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

        // Write LLM output to working memory if output_to is specified
        if (!string.IsNullOrEmpty(step.OutputTo))
        {
            var outputKey = $"{wispNamespace}/{step.Id}/output";
            await workingMemory.SetAsync(outputKey, llmOutput,
                ttl: TimeSpan.FromMinutes(60), category: "wisp-output");
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
    /// Resolves an input_from reference to actual content. Handles:
    /// - Template references like {{steps.id.result}}
    /// - Working memory keys from prior step output_to
    /// </summary>
    private static string? ResolveInputFrom(string inputFrom, string wispNamespace, IReadOnlyDictionary<string, WispStepResult> priorResults)
    {
        // Check if it's a template reference
        var resolved = GatewayRouter.ResolveTemplateString(inputFrom, priorResults);
        if (resolved != inputFrom)
            return resolved;

        // Check if it matches a prior step's content by step ID pattern
        // e.g., if inputFrom is a file path like /shared/wisp-abc/parsed.json,
        // the actual data is in working memory from the prior step's output_to
        foreach (var (stepId, result) in priorResults)
        {
            if (result.Content is not null && !string.IsNullOrEmpty(result.Content))
            {
                // If any prior step wrote to this path (matching output_to), use its content
                // This is a simplified resolution — full shared volume I/O comes in Phase 2
                return result.Content;
            }
        }

        return null;
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

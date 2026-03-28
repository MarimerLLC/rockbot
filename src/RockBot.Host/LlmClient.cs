using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Default implementation of <see cref="ILlmClient"/>.
/// Selects the appropriate <see cref="IChatClient"/> from the
/// <see cref="TieredChatClientRegistry"/> and adds retry logic for
/// known model-specific SDK quirks. Registered as transient so each consumer
/// gets its own instance — concurrent calls from the user loop, background tasks,
/// dreaming, and session evaluation proceed independently without queuing.
/// </summary>
internal sealed class LlmClient(
    TieredChatClientRegistry registry,
    IOptions<AgentHostOptions> hostOptions,
    ILogger<LlmClient> logger) : ILlmClient
{
    private readonly TimeSpan _callTimeout = hostOptions.Value.LlmCallTimeout;
    /// <summary>Calls the LLM using the Balanced tier.</summary>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetResponseAsync(messages, ModelTier.Balanced, options, cancellationToken);

    /// <summary>Calls the LLM using the specified tier, falling back to Balanced on failure for Low/High tiers.</summary>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ModelTier tier,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await CallTierAsync(messages, tier, options, cancellationToken);
        }
        catch (Exception ex) when (tier != ModelTier.Balanced && ex is not OperationCanceledException)
        {
            // Low/High tier failed — fall back to Balanced (which may have its own FallbackChatClient chain)
            logger.LogWarning(ex, "LLM call failed on {Tier} tier — falling back to Balanced", tier);
            return await CallTierAsync(messages, ModelTier.Balanced, options, cancellationToken);
        }
    }

    private async Task<ChatResponse> CallTierAsync(
        IEnumerable<ChatMessage> messages,
        ModelTier tier,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var client = registry.GetClient(tier);
        var modelId = registry.GetModelId(tier) ?? tier.ToString();
        logger.LogInformation("LLM call: tier={Tier} model={ModelId}", tier, modelId);

        using var activity = HostDiagnostics.Source.StartActivity("rockbot.llm.call");
        activity?.SetTag("rockbot.llm.tier", tier.ToString());
        activity?.SetTag("rockbot.llm.model", modelId);

        var sw = Stopwatch.StartNew();
        var status = "ok";
        try
        {
            // Apply per-call timeout: if the LLM provider stalls, abort before the
            // 5-minute HTTP NetworkTimeout and surface as TimeoutException so evaluators
            // can fail-open instead of hanging.
            CancellationToken effectiveCt;
            CancellationTokenSource? timeoutCts = null;
            if (_callTimeout > TimeSpan.Zero)
            {
                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_callTimeout);
                effectiveCt = timeoutCts.Token;
            }
            else
            {
                effectiveCt = cancellationToken;
            }

            try
            {
                var response = await InvokeWithNullArgRetryAsync(client, messages, options, effectiveCt);

                if (response.Usage is { } usage)
                {
                    var inputTokens = usage.InputTokenCount ?? 0;
                    var outputTokens = usage.OutputTokenCount ?? 0;

                    var tierTag = new KeyValuePair<string, object?>("rockbot.llm.tier", tier.ToString());
                    var modelTag = new KeyValuePair<string, object?>("rockbot.llm.model", modelId);

                    if (usage.InputTokenCount.HasValue)
                        HostDiagnostics.LlmTokenInput.Add(inputTokens, tierTag, modelTag);
                    if (usage.OutputTokenCount.HasValue)
                        HostDiagnostics.LlmTokenOutput.Add(outputTokens, tierTag, modelTag);

                    var costUsd = LlmCostEstimator.EstimateCost(modelId, inputTokens, outputTokens);
                    if (costUsd > 0)
                    {
                        HostDiagnostics.LlmCostUsd.Add(costUsd, tierTag, modelTag);
                        HostDiagnostics.LlmCostPerRequest.Record(costUsd, tierTag, modelTag);
                    }

                    activity?.SetTag("rockbot.llm.tokens.input", inputTokens);
                    activity?.SetTag("rockbot.llm.tokens.output", outputTokens);
                    activity?.SetTag("rockbot.llm.cost.usd", costUsd);
                }

                activity?.SetStatus(ActivityStatusCode.Ok);
                return response;
            }
            catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Per-call timeout fired (not the caller's token) — convert to TimeoutException
                // so evaluators' catch-all (ex is not OperationCanceledException) can fail-open.
                logger.LogWarning("LLM call timed out after {Timeout} on {Tier} tier ({Model})",
                    _callTimeout, tier, modelId);
                throw new TimeoutException(
                    $"LLM call to {modelId} ({tier}) timed out after {_callTimeout.TotalSeconds:F0}s");
            }
            finally
            {
                timeoutCts?.Dispose();
            }
        }
        catch (Exception) when (status == "ok")
        {
            status = "error";
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            sw.Stop();
            activity?.SetTag("rockbot.llm.status", status);
            HostDiagnostics.LlmRequestDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("rockbot.llm.status", status),
                new KeyValuePair<string, object?>("rockbot.llm.tier", tier.ToString()),
                new KeyValuePair<string, object?>("rockbot.llm.model", modelId));
            HostDiagnostics.LlmRequests.Add(1,
                new KeyValuePair<string, object?>("rockbot.llm.status", status),
                new KeyValuePair<string, object?>("rockbot.llm.tier", tier.ToString()),
                new KeyValuePair<string, object?>("rockbot.llm.model", modelId));
        }
    }

    /// <summary>
    /// Calls the underlying chat client, retrying once on known model-specific SDK
    /// deserialization quirks:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <b>Null tool-call arguments</b> — some models omit the arguments JSON entirely
    ///       (returning <c>null</c> instead of <c>{}</c>), causing
    ///       <see cref="ArgumentNullException"/> with <c>encodedArguments</c>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <b>Unknown finish_reason</b> — some models (e.g. DeepSeek, Qwen via OpenRouter)
    ///       return finish reason values the OpenAI SDK does not recognise, causing
    ///       <see cref="ArgumentOutOfRangeException"/> from
    ///       <c>ChatFinishReasonExtensions.ToChatFinishReason</c>. A single retry usually
    ///       produces a well-formed response.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    private async Task<ChatResponse> InvokeWithNullArgRetryAsync(
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (ArgumentNullException ex) when (ex.ParamName == "encodedArguments")
        {
            logger.LogWarning(
                "LLM returned a tool call with null arguments (encodedArguments); retrying once");
            return await client.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.Message.Contains("ChatFinishReason"))
        {
            logger.LogWarning(
                "LLM returned an unrecognised finish_reason; retrying once. Detail: {Message}", ex.Message);
            return await client.GetResponseAsync(messages, options, cancellationToken);
        }
    }
}

using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Default implementation of <see cref="ILlmClient"/>.
/// Selects the appropriate <see cref="IChatClient"/> from the
/// <see cref="TieredChatClientRegistry"/>, adds retry logic for known
/// model-specific SDK quirks, and routes the call through the singleton
/// <see cref="ILlmGateway"/> which caps per-tier concurrency.
/// </summary>
internal sealed class LlmClient(
    TieredChatClientRegistry registry,
    ILlmGateway gateway,
    LlmCostEstimator costEstimator,
    ILogger<LlmClient> logger) : ILlmClient
{
    /// <summary>Calls the LLM using the Balanced tier.</summary>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
        => GetResponseAsync(messages, ModelTier.Balanced, options, cancellationToken);

    /// <summary>Calls the LLM using the specified tier, falling back to Balanced on failure for Low/High tiers.</summary>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ModelTier tier,
        ChatOptions? options,
        CancellationToken cancellationToken)
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
            // Per-call timeout is handled inside FallbackChatClient as a per-attempt
            // timeout — each model in the fallback chain gets its own timeout window.
            // This ensures a stalled primary model doesn't prevent fallback to the
            // next model. The original cancellation token is passed through so
            // FallbackChatClient can correctly distinguish user cancellation from
            // provider timeouts.
            //
            // The gateway gates the actual SDK call on the per-tier concurrency
            // semaphore so bursty parallel callers cannot overwhelm a tier.
            var response = await gateway.ExecuteAsync(
                tier,
                ct => InvokeWithNullArgRetryAsync(client, messages, options, ct),
                cancellationToken);

            if (response.Usage is { } usage)
            {
                var inputTokens = usage.InputTokenCount ?? 0;
                var outputTokens = usage.OutputTokenCount ?? 0;
                var cachedInputTokens = UsageReader.GetCachedInputTokens(usage);

                var tierTag = new KeyValuePair<string, object?>("rockbot.llm.tier", tier.ToString());
                var modelTag = new KeyValuePair<string, object?>("rockbot.llm.model", modelId);

                if (usage.InputTokenCount.HasValue)
                    HostDiagnostics.LlmTokenInput.Add(inputTokens, tierTag, modelTag);
                if (usage.OutputTokenCount.HasValue)
                    HostDiagnostics.LlmTokenOutput.Add(outputTokens, tierTag, modelTag);
                if (cachedInputTokens > 0)
                    HostDiagnostics.LlmTokenInputCached.Add(cachedInputTokens, tierTag, modelTag);

                var costUsd = costEstimator.EstimateCost(modelId, inputTokens, outputTokens);
                if (costUsd > 0)
                {
                    HostDiagnostics.LlmCostUsd.Add(costUsd, tierTag, modelTag);
                    HostDiagnostics.LlmCostPerRequest.Record(costUsd, tierTag, modelTag);
                }

                activity?.SetTag("rockbot.llm.tokens.input", inputTokens);
                activity?.SetTag("rockbot.llm.tokens.output", outputTokens);
                activity?.SetTag("rockbot.llm.tokens.input.cached", cachedInputTokens);
                activity?.SetTag("rockbot.llm.cost.usd", costUsd);

                if (inputTokens > 0)
                {
                    var cachePct = cachedInputTokens * 100.0 / inputTokens;
                    var extra = UsageReader.DescribeAdditionalCounts(usage);
                    logger.LogInformation(
                        "LLM usage: tier={Tier} model={ModelId} input={InputTokens} cached={CachedTokens} ({CachePct:F1}%) output={OutputTokens} additionalCounts=[{Extra}]",
                        tier, modelId, inputTokens, cachedInputTokens, cachePct, outputTokens,
                        string.IsNullOrEmpty(extra) ? "(none)" : extra);
                }
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            return response;
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
